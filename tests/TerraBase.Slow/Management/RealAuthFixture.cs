extern alias tsauth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.UserAuth;
using TerraBase.Primitives;
using TerraBase.Testing;
using tsauth::TerraScale.Authentication.Configuration;
using tsauth::TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraBase.Slow.Management;

/// <summary>
/// The real ts-auth application, built from this monorepo and run in process as its own tests run it: its storage is a second
/// in-process TerraBase from this commit, reached through the internal listener with the peer credential, its public origin
/// <see cref="PublicOrigin"/> is served over TLS on a
/// loopback port, and one service client gets client-credentials access tokens. Tokens, discovery and JWKS all come from
/// ts-auth itself; the TerraBase under test reaches them through its production HTTP client, only connected to that port.
/// ts-auth's GitHub OIDC client reaches <see cref="GitHub"/> instead of GitHub.
/// </summary>
internal sealed class RealAuthFixture : IAsyncDisposable
{
    public const string Audience = GitHubActionsGrant.ApiAudience;
    private const string PublicOrigin = "https://auth.test.invalid";

    private readonly string _clientSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly X509Certificate2 _certificate = Certificate();
    private TerraBaseTestServer? _storage;
    private WebApplication? _auth;
    private HttpClient? _tokens;
    private int _tlsPort;

    public string Subject { get; } = EntityIds.New().ToString();

    /// <summary>GitHub Actions' OIDC issuer as ts-auth's own suite fakes it, for <see cref="GitHubActionsTokenAsync"/>.</summary>
    public FakeGitHubActions GitHub { get; } = new();

    /// <summary>The <c>terrascale</c> tenant's issuer, which every platform access token names.</summary>
    public static string Issuer => PublicOrigin + "/terrascale";

    public static async Task<RealAuthFixture> StartAsync(CancellationToken token)
    {
        var fixture = new RealAuthFixture();
        try
        {
            await fixture.StartCoreAsync(token);
            return fixture;
        }
        catch (Exception startupError)
        {
            try
            {
                await fixture.DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(startupError, cleanupError);
            }

            throw;
        }
    }

    /// <summary>The settings that make a TerraBase server trust this ts-auth.</summary>
    public static void Configure(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings["auth-issuer"] = Issuer;
        settings["auth-audience"] = Audience;
    }

    /// <summary>
    /// Points the production ts-auth HTTP client of a TerraBase server at this ts-auth's TLS port, trusting only its
    /// certificate. The handler is the production one, configured, never replaced.
    /// </summary>
    public void TrustIssuer(IServiceCollection services) =>
        services.AddHttpClient(TsAuthKeys.HttpClientName).ConfigurePrimaryHttpMessageHandler((handler, _) =>
        {
            var sockets = (SocketsHttpHandler)handler;
            sockets.ConnectCallback = ConnectAsync;
            sockets.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
                presented is not null && presented.GetCertHash().AsSpan().SequenceEqual(_certificate.GetCertHash());
        });

    /// <summary>An access token of the service client, from ts-auth's token endpoint (client credentials).</summary>
    public async Task<string> AccessTokenAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/terrascale/oauth/token", UriKind.Relative))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Subject}:{_clientSecret}")));
        return await TokenAsync(request, token);
    }

    /// <summary>A separate fixture-only project key used to seed real target data.</summary>
    public async Task<(string Organization, string Project, string Token)> SeedProjectAsync(CancellationToken token)
    {
        var services = _auth!.Services;
        var users = services.GetRequiredService<tsauth::TerraScale.Authentication.Features.Identity.Users>();
        var created = (await users.CreateAsync(PlatformDirectory.Instance, $"e2e-{EntityIds.New()}@example.test", token))!;
        // A fixture seed issues its key as a freshly stepped-up sign-in would, bound to the account's current credentials.
        var current = (await users.GetAsync(PlatformDirectory.Instance, created.Id, token))!;
        var user = current with { StepUpBindings = current.AuthenticationBindings };
        var directory = services.GetRequiredService<PlatformDirectory>();
        var organization = await directory.CreateOrganizationAsync(user, "Isolated E2E", token);
        var project = await directory.CreateProjectAsync(user.Id, organization.Id, "E2E", token);
        var deadline = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        var (key, secret) = await services.GetRequiredService<ApiKeys>().CreateAsync(user, organization.Id, project.Id, "fixture seed",
            [TerraBaseScopes.DatabasesRead, TerraBaseScopes.DatabasesWrite, TerraBaseScopes.DocumentsRead, TerraBaseScopes.DocumentsWrite], null, deadline, [], token);
        using var seedRequest = new HttpRequestMessage(HttpMethod.Post, new Uri("/terrascale/oauth/token", UriKind.Relative))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = key.Id,
                ["client_secret"] = secret,
            }),
        };
        return (organization.Id, project.Id, await TokenAsync(seedRequest, token));
    }

    /// <summary>A real read-only workload token bound to the existing database <paramref name="database"/> (its ULID).</summary>
    public async Task<string> GitHubActionsTokenAsync(string organization, string project, string database, CancellationToken token)
    {
        var deadline = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        await GitHubActionsBinding.BindAsync(_auth!.Services, organization, project, database, deadline, token);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/terrascale/oauth/token", UriKind.Relative))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = GitHubActionsGrant.GrantType,
                ["client_id"] = GitHubActionsGrant.ClientId,
                ["subject_token"] = GitHub.Mint(GitHubActionsGrant.Audience),
            }),
        };
        return await TokenAsync(request, token);
    }

    private async Task<string> TokenAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await _tokens!.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return body.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("ts-auth answered without an access token");
    }

    private async Task StartCoreAsync(CancellationToken token)
    {
        // ts-auth's own storage, not the TerraBase under test: a project database on another TerraBase from this commit,
        // reached through its internal listener as the platform operator. ts-auth creates its collections itself; its first
        // signing key comes from the operator provisioning below.
        _storage = await TerraBaseTestServer.StartAsync(cancellationToken: token);
        var management = new TerraBaseManagementClient(_storage.Client, new TerraBaseManagementClientOptions { BaseAddress = _storage.BaseAddress });
        var owner = EntityIds.New().ToString();
        var organization = await management.CreateOrganizationAsync(owner, "ts-auth storage", token);
        await management.SetPlanAsync(owner, organization.Id, TerraBasePlan.Enterprise, token);
        var project = await management.CreateProjectAsync(owner, organization.Id, "ts-auth", token);
        var storage = new TerraBaseDocumentsClient(_storage.Client, new TerraBaseDocumentsClientOptions { BaseAddress = _storage.BaseAddress })
            .Database(project.Id, "auth");
        await storage.CreateAsync(token);

        var options = AuthOptions.Load(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["listen"] = "http://127.0.0.1:0",
            ["metrics-listen"] = "http://127.0.0.1:0",
            ["public-origin"] = PublicOrigin,
            ["master-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["audit-signing-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["environment"] = "Development",
            ["service-clients"] = $"{Subject}:{_clientSecret}",
            ["access-token-audiences"] = Audience,
        });
        _auth = tsauth::TerraScale.Authentication.App.Build(options, builder =>
        {
            builder.Services.AddSingleton(_storage.Client);
            builder.Services.AddSingleton(storage);
            GitHub.Route(builder.Services);
            // The public origin over TLS, after ts-auth's own listeners, for TerraBase's discovery and JWKS fetches.
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(_certificate)));
        });
        // As an operator does once per empty platform database (`ts-auth provision-signing-key`): ts-auth never creates the
        // issuer's first signing key itself (docs/auth/signing-keys.md). The master key is this fixture's synthetic one.
        await tsauth::TerraScale.Authentication.Features.SigningKeys.SigningKeyProvisioning.ProvisionAsync(_auth.Services, token);
        await _auth.StartAsync(token);
        // Bound in order: ts-auth's public listener, its metrics listener, then the TLS port.
        var addresses = _auth.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(address => new Uri(address)).ToArray();
        _tlsPort = addresses[2].Port;
        _tokens = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = addresses[0], Timeout = TimeSpan.FromSeconds(30) };

        // Ready once its signing ring exists in storage and its public signing metadata serves.
        await TerraBaseTestServer.EventuallyAsync(async deadline =>
        {
            using var ready = await _tokens.GetAsync(new Uri("/readyz", UriKind.Relative), deadline);
            return ready.StatusCode == HttpStatusCode.OK;
        });
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, _tlsPort, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=auth.test.invalid", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("auth.test.invalid");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Kestrel needs the private key in a persistable form on Linux.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), password: null);
    }

    public async ValueTask DisposeAsync()
    {
        _tokens?.Dispose();
        try
        {
            if (_auth is not null)
            {
                await _auth.DisposeAsync();
            }
        }
        finally
        {
            try
            {
                if (_storage is not null)
                {
                    await _storage.DisposeAsync();
                }
            }
            finally
            {
                _certificate.Dispose();
                GitHub.Dispose();
            }
        }
    }
}
