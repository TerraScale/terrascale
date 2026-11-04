using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.Tests.TerraBase;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests;

/// <summary>
/// The ts-auth to TerraBase token contract (TerraBaseOptions.PlatformAuthIssuer/PlatformAuthAudience,
/// AuthOptions.GlobalOrigin/TerraBaseAudience, TerraBaseScopes): one global issuer, one exact audience, four scopes
/// sent as one space-separated form value, and 401 kept apart from 403. Fakes only; nothing leaves the process.
/// </summary>
public sealed class TerraScaleAuthTokenContractTests
{
    private const string Issuer = "https://auth.terrascale.tech/terrascale";
    private const string Audience = "https://api.terrascale.tech";
    private static readonly string[] Scopes = ["databases:read", "databases:write", "documents:read", "documents:write"];
    private const string TokenJson = "{\"access_token\":\"fixture-token\",\"token_type\":\"Bearer\",\"expires_in\":900,\"scope\":\"documents:read\"}";

    [Test]
    public async Task PinnedSecurityContractNamesTheOneIssuerAudienceAndFourScopes()
    {
        using var security = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath("security.json")));
        var audiences = security.RootElement.GetProperty("token_audiences");
        await Assert.That(audiences.GetProperty("platform_issuer").GetString()).IsEqualTo(Issuer);
        await Assert.That(audiences.GetProperty("resource").GetString()).IsEqualTo(Audience);
        await Assert.That(audiences.GetProperty("regional_host_changes_audience").GetBoolean()).IsFalse();
        await Assert.That(security.RootElement.GetProperty("action_scopes").EnumerateArray().Select(scope => scope.GetString()!).ToArray())
            .IsEquivalentTo(Scopes);
    }

    [Test]
    [Arguments("documents:read")]
    [Arguments("databases:read documents:read")]
    [Arguments("databases:read databases:write documents:read documents:write")]
    public async Task ClientCredentialsSendsTheExactSpaceSeparatedScope(string scope)
    {
        using var handler = new CaptureHandler(HttpStatusCode.OK, TokenJson);
        using var http = new HttpClient(handler);
        await Client(http).Oidc.RequestClientCredentialsTokenAsync("01JA0000000000000000000001", "fixture-secret", scope);
        var form = Form(handler.Bodies[0]);
        await Assert.That(form["grant_type"]).IsEqualTo("client_credentials");
        await Assert.That(form["scope"]).IsEqualTo(scope);
        await Assert.That(scope.Split(' ').All(Scopes.Contains)).IsTrue();
    }

    [Test]
    public async Task TokenExchangeSendsTheExactScopeAndProject()
    {
        using var handler = new CaptureHandler(HttpStatusCode.OK, TokenJson);
        using var http = new HttpClient(handler);
        await Client(http).Oidc.ExchangeTokenAsync("fixture-client", "fixture-secret", "fixture-subject",
            "01JA0000000000000000000002", "01JA0000000000000000000003", "databases:read documents:read");
        var form = Form(handler.Bodies[0]);
        await Assert.That(form["grant_type"]).IsEqualTo("urn:ietf:params:oauth:grant-type:token-exchange");
        await Assert.That(form["scope"]).IsEqualTo("databases:read documents:read");
        await Assert.That(form["organization"]).IsEqualTo("01JA0000000000000000000002");
        await Assert.That(form["project"]).IsEqualTo("01JA0000000000000000000003");
        await Assert.That(handler.Authorizations[0]!.Scheme).IsEqualTo("Basic");
    }

    [Test]
    [Arguments(HttpStatusCode.Unauthorized, "unauthorized", typeof(TerraBaseAuthenticationException))]
    [Arguments(HttpStatusCode.Forbidden, "forbidden_scope", typeof(TerraBaseForbiddenException))]
    public async Task TerraBase401And403AreDistinctAndNeverReauthenticate(HttpStatusCode status, string code, Type type)
    {
        var sends = 0;
        var tokens = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            Interlocked.Increment(ref sends);
            return Task.FromResult(TerraBaseIntegrationFixture.Json("{\"error\":{\"code\":\"" + code + "\",\"message\":\"refused\"}}", status));
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
        {
            Region = "sjc",
            AccessTokenProvider = _ => { Interlocked.Increment(ref tokens); return ValueTask.FromResult("project-token"); },
        });
        var error = await Assert.ThrowsAsync<TerraBaseException>(() =>
            client.Database(TerraBaseIntegrationFixture.ProjectId, "app").ListCollectionsAsync());
        await Assert.That(error!.GetType()).IsEqualTo(type);
        await Assert.That(error.ErrorCode).IsEqualTo(code);
        await Assert.That(sends).IsEqualTo(1);
        await Assert.That(tokens).IsEqualTo(1);
    }

    // ToDictionary throws on a repeated field, so each value (scope included) is sent exactly once.
    private static Dictionary<string, string> Form(string body) => body.Split('&')
        .Select(pair => pair.Split('=', 2).Select(part => Uri.UnescapeDataString(part.Replace('+', ' '))).ToArray())
        .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

    private static string ContractPath(string relative, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../contracts/auth", relative));

    private static TerraScaleAuthClient Client(HttpClient http, string origin = "https://auth.terrascale.tech") => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri(origin),
        InstanceSlug = "terrascale",
    });

    private sealed class CaptureHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        public List<string> Bodies { get; } = [];
        public List<AuthenticationHeaderValue?> Authorizations { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            Authorizations.Add(request.Headers.Authorization);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
