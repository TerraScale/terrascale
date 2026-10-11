using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.UserAuth;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

// Reuses ts-auth's TerraBaseFixture/OidcHarness setup, with real immutable executables instead of an in-process Auth host.
internal sealed class RealAuthFixture : IAsyncDisposable
{
    public const string Audience = "https://terrabase.test";
    private const string Organizations = "/v1/management/organizations";
    private static readonly string[] StorageScopes = ["documents:read", "documents:write"];
    private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-real-auth-").FullName;
    private readonly ClientTls _tls = new();
    private readonly List<AuthFixtureProcess> _processes = [];
    private readonly string _clientSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private RealAuthTransport? _transport;
    private HttpClient? _auth;

    public string Subject { get; } = EntityIds.New().ToString();
    public string Issuer => new Uri(_transport!.PublicOrigin, "terrascale").AbsoluteUri;

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

    public void Configure(Dictionary<string, string> settings)
    {
        settings["auth-issuer"] = Issuer;
        settings["auth-audience"] = Audience;
    }

    public void TrustIssuer(IServiceCollection services) => services.AddHttpClient(TsAuthKeys.HttpClientName).ConfigurePrimaryHttpMessageHandler(_tls.CreateHandler);

    public async Task<string> AccessTokenAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/terrascale/oauth/token", UriKind.Relative))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Subject}:{_clientSecret}")));
        using var response = await _auth!.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return body.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Auth answered without an access token");
    }

    private async Task StartCoreAsync(CancellationToken token)
    {
        var repository = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (!File.Exists(Path.Combine(repository.FullName, "TerraScale.slnx")))
        {
            repository = repository.Parent ?? throw new InvalidOperationException("run the Slow suite from the TerraBase checkout");
        }

        var artifacts = Path.Combine(_directory, "artifacts");
        using (var acquisitionDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            acquisitionDeadline.CancelAfter(TimeSpan.FromMinutes(5));
            await using var acquisition = AuthFixtureProcess.Start("bash", _directory,
                [Path.Combine(repository.FullName, "tests/TerraScale.TerraBase.Slow/Management/acquire-auth-fixture.sh"), artifacts],
                Environment.GetEnvironmentVariable("FORGEJO_TOKEN") is { } downloadToken
                    ? new Dictionary<string, string> { ["FORGEJO_TOKEN"] = downloadToken } : null);
            await acquisition.CompleteAsync(acquisitionDeadline.Token);
        }

        var credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var credentialFile = Path.Combine(_directory, "storage-credential");
        await File.WriteAllTextAsync(credentialFile, credential, token);
        var settings = new Dictionary<string, string>
        {
            ["admin-port"] = "0",
            ["data-directory"] = Path.Combine(_directory, "storage"),
            ["dashboard-credential-file"] = credentialFile,
        };
        _tls.Apply(settings);
        // Auth's qualified pin uses UUID documents; it is solely Auth's private storage, never the target under test.
        var storage = AuthFixtureProcess.Start(Path.Combine(artifacts, "terrabase"), _directory, settings.Select(pair => $"--{pair.Key}={pair.Value}"));
        _processes.Add(storage);
        var adminAddress = await storage.HttpAsync(token);
        var clientAddress = await storage.HttpsAsync(token);
        using var admin = new HttpClient { BaseAddress = adminAddress, Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1024 * 1024 };
        using var database = _tls.CreateClient(clientAddress, credential);
        database.MaxResponseContentBufferSize = 1024 * 1024;
        database.DefaultRequestHeaders.Add("X-TerraBase-User-Id", Subject);
        var organization = await CreateAsync(database, Organizations, new { name = "Auth fixture" }, token);
        database.DefaultRequestHeaders.Add("X-TerraBase-Organization-Id", organization.GetProperty("id").GetString());
        var project = await CreateAsync(database, Organizations + "/" + organization.GetProperty("id").GetString() + "/projects", new { name = "Auth fixture" }, token);
        var projectId = project.GetProperty("id").GetString()!;
        var apiKey = await CreateAsync(admin, "/admin/api-keys", new
        {
            name = "Auth storage",
            scopes = StorageScopes,
            organizationId = organization.GetProperty("id").GetString(),
        }, token);
        database.DefaultRequestHeaders.Clear();
        database.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.GetProperty("secret").GetString());
        var databasePath = "/v1/projects/" + projectId + "/databases/auth";
        using (var created = await database.PutAsync(new Uri(databasePath, UriKind.Relative), content: null, token))
        {
            created.EnsureSuccessStatusCode();
        }

        // AuthStore.RequiredCollections at the qualified Auth revision: readiness requires the entire set.
        string[] collections = ["uniques", "verification_tokens", "storage_health", "users", "sessions", "passcodes", "signing_key_rings",
            "oidc_applications", "oidc_authorizations", "oidc_tokens", "rate_limits", "email_configs", "email_config_versions",
            "organizations", "memberships", "projects", "project_members", "api_keys", "invitations", "audit_events", "mfa_credentials"];
        foreach (var collection in collections)
        {
            _ = await CreateAsync(database, databasePath + "/collections", new { name = collection }, token);
        }

        _transport = await RealAuthTransport.StartAsync(_tls, token);
        var environment = new Dictionary<string, string>
        {
            ["TS_AUTH_ENVIRONMENT"] = "Development",
            ["TS_AUTH_TERRASCALE_API_URL"] = clientAddress.GetLeftPart(UriPartial.Authority),
            ["TS_AUTH_TERRASCALE_API_ENDPOINTS"] = clientAddress.GetLeftPart(UriPartial.Authority),
            ["TS_AUTH_TERRASCALE_API_CA"] = _tls.CertificatePem,
            ["TS_AUTH_TERRASCALE_API_KEY"] = apiKey.GetProperty("secret").GetString()!,
            ["TS_AUTH_DATABASE"] = projectId + "/auth",
            ["TS_AUTH_MASTER_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["TS_AUTH_OPERATOR_KEY"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ["TS_AUTH_ACCESS_TOKEN_AUDIENCES"] = Audience,
            ["TS_AUTH_SERVICE_CLIENTS"] = Subject + ":" + _clientSecret,
            ["TS_AUTH_SIGNING_KEY_VAULT"] = RealAuthTransport.VaultId,
            ["TS_AUTH_SIGNING_KEY_SECRET"] = RealAuthTransport.SecretName,
            ["TS_AUTH_OCI_REGION"] = "sa-saopaulo-1",
            ["TS_AUTH_OCI_TENANCY"] = "ocid1.tenancy.oc1..aaaatesttenancy",
            ["TS_AUTH_OCI_USER"] = "ocid1.user.oc1..aaaatestuser",
            ["TS_AUTH_OCI_FINGERPRINT"] = "00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff",
            ["TS_AUTH_OCI_PRIVATE_KEY"] = _transport.PrivateKeyPem,
            ["TS_AUTH_OCI_ENDPOINT"] = _transport.VaultOrigin.AbsoluteUri,
        };
        var auth = AuthFixtureProcess.Start(Path.Combine(artifacts, "ts-auth"), _directory,
            ["--listen=http://127.0.0.1:0", "--public-origin=" + _transport.PublicOrigin.GetLeftPart(UriPartial.Authority)], environment);
        _processes.Add(auth);
        _transport.AuthAddress = await auth.HttpAsync(token);
        await auth.SigningRingAsync(token);
        _auth = _tls.CreateClient(_transport.PublicOrigin);
        _auth.MaxResponseContentBufferSize = 1024 * 1024;
    }

    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T body, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(token);
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var process in _processes.AsEnumerable().Reverse())
        {
            try
            {
                await process.DisposeAsync();
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        _auth?.Dispose();
        if (_transport is not null)
        {
            try
            {
                await _transport.DisposeAsync();
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        _tls.Dispose();
        if (failures.Count != 0)
        {
            // Retain the isolated directory if process exit could not be confirmed.
            throw new AggregateException(failures);
        }

        Directory.Delete(_directory, recursive: true);
    }
}
