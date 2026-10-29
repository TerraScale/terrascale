using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.E2E.Features.Accounts;

// The production TsAuthGateway exchanges the signed-in user's token at a producer-shaped
// ts-auth issuer. The TerraBase side records every request before checking anything, so a
// project token the dashboard should have refused is visible if it is ever dispatched.
public sealed class ProjectTokenJourneyTests
{
    [Test]
    public async Task ColdProjectExchangeOverlapsDiscoveryAndSigningKeys()
    {
        await using var scenario = await TokenScenario.StartAsync();
        using var client = scenario.Client();
        using (var login = await HttpJourney.LoginAsync(client)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        scenario.Issuer.RequireOverlap = true;
        using var page = await client.GetAsync("/analytics?range=24h");
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(scenario.Issuer.PublicReads).IsEqualTo(2);
        await Assert.That(scenario.Issuer.Exchanges).IsEqualTo(1);
        await Assert.That(scenario.Recorder.Bearers.Count).IsEqualTo(1).Because("all three issuer requests must start before any can finish");
        await Assert.That(scenario.Issuer.Failures).IsEmpty();
    }

    [Test]
    public async Task ExpiredOrForeignProjectTokensAreNeverDispatchedToTerraBase()
    {
        await using var scenario = await TokenScenario.StartAsync();
        using var client = scenario.Client();
        using (var login = await HttpJourney.LoginAsync(client)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        var projectId = me.RootElement.GetProperty("projectId").GetString()!;

        foreach (var fault in new[] { "expired", "foreign-project", "wrong-audience", "extra-database" })
        {
            scenario.Issuer.Fault = fault;
            using var page = await client.GetAsync("/analytics?range=24h");
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(fault);
            await Assert.That(await page.Content.ReadAsStringAsync()).Contains("Analytics unavailable").Because(fault);
        }
        await Assert.That(scenario.Issuer.Exchanges).IsEqualTo(4);
        await Assert.That(scenario.Recorder.Bearers).IsEmpty().Because("a refused project token must never reach TerraBase");

        scenario.Issuer.Fault = "";
        using (var page = await client.GetAsync("/analytics?range=24h")) await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(scenario.Issuer.Exchanges).IsEqualTo(5);
        var dispatched = scenario.Recorder.Bearers.Single();
        await Assert.That(dispatched.Path).IsEqualTo("/v1/projects/" + projectId + "/analytics");
        await Assert.That(dispatched.Token).IsEqualTo(scenario.Issuer.LastToken);
        await Assert.That(scenario.Issuer.LastScope).IsEqualTo("databases:read");
        await Assert.That(scenario.Issuer.Failures).IsEmpty();
    }

    [Test]
    public async Task OneExchangedProjectTokenServesRepeatedPageLoads()
    {
        await using var scenario = await TokenScenario.StartAsync();
        using var client = scenario.Client();
        using (var login = await HttpJourney.LoginAsync(client)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        for (var load = 0; load < 3; load++)
        {
            using var page = await client.GetAsync("/analytics?range=24h");
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(page.Headers.GetValues("Server-Timing").Single()).StartsWith("app;dur=");
        }
        await Assert.That(scenario.Issuer.Exchanges).IsEqualTo(1).Because("a page reuses the session's exchanged project token instead of asking ts-auth again");
        await Assert.That(scenario.Recorder.Bearers.Select(bearer => bearer.Token).Distinct().Single()).IsEqualTo(scenario.Issuer.LastToken);

        // Another session never shares the token, and a failed exchange is never reused: its next load asks ts-auth again.
        using var other = scenario.Client();
        using (var login = await HttpJourney.LoginAsync(other)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        scenario.Issuer.Fault = "expired";
        using (var page = await other.GetAsync("/analytics?range=24h")) await Assert.That(await page.Content.ReadAsStringAsync()).Contains("Analytics unavailable");
        scenario.Issuer.Fault = "";
        using (var page = await other.GetAsync("/analytics?range=24h")) await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(scenario.Issuer.Exchanges).IsEqualTo(3);
        await Assert.That(scenario.Issuer.Failures).IsEmpty();
    }

    private sealed class TokenScenario : IAsyncDisposable
    {
        private readonly DemoEnvironment environment = new();
        private WebApplication? host;
        private Uri origin = null!;
        public Issuer Issuer { get; private set; } = null!;
        public Recorder Recorder { get; } = new();

        public static async Task<TokenScenario> StartAsync()
        {
            var scenario = new TokenScenario();
            try
            {
                await scenario.environment.InitializeAsync();
                scenario.host = scenario.environment.CreateTestHost(Root, 0, "http://127.0.0.1", builder =>
                {
                    var auth = (TestAuth)builder.Services.Last(service => service.ServiceType == typeof(IAuthGateway)).ImplementationInstance!;
                    scenario.Issuer = new(auth.ResolveAccessToken);
                    builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new Factory(scenario.Issuer, scenario.Recorder)));
                    // The production exchange and token validation, not the in-process project-token fake.
                    builder.Services.Replace(ServiceDescriptor.Singleton<IProjectAccessTokens, TsAuthGateway>());
                });
                await scenario.host.StartAsync();
                scenario.origin = new(scenario.host.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
                return scenario;
            }
            catch { await scenario.DisposeAsync(); throw; }
        }

        public HttpClient Client() => new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };

        public async ValueTask DisposeAsync()
        {
            if (host is not null) { await host.StopAsync(); await host.DisposeAsync(); host = null; }
        }

        private static string Root { get; } = FindRoot();
        private static string FindRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "TerraScale.Dashboard.slnx"))) return directory.FullName;
            throw new DirectoryNotFoundException("The E2E executable must run from the dashboard checkout.");
        }
    }

    private sealed class Factory(Issuer issuer, Recorder recorder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Router(issuer, recorder));
    }

    private sealed class Router(Issuer issuer, Recorder recorder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.Host == "auth.terrascale.tech" ? issuer.HandleAsync(request, cancellationToken) : Task.FromResult(recorder.Handle(request));
    }

    // Every request bound for TerraBase is recorded before anything is checked.
    private sealed class Recorder
    {
        private readonly ConcurrentQueue<(string Path, string Token)> bearers = new();
        public IReadOnlyList<(string Path, string Token)> Bearers => bearers.ToArray();
        public HttpResponseMessage Handle(HttpRequestMessage request)
        {
            if (request.Headers.Authorization is { Scheme: "Bearer", Parameter: { } token }) bearers.Enqueue((request.RequestUri!.AbsolutePath, token));
            return new(HttpStatusCode.ServiceUnavailable);
        }
    }

    // ts-auth's issuer, JWKS and RFC 8693 exchange (terrascale e30ffdc2 PlatformGrants.cs).
    private sealed class Issuer(Func<string, (string UserId, string Email)?> resolve)
    {
        private const string Origin = "https://auth.terrascale.tech/terrascale";
        private const string ClientId = "01M4EQ2FRJ8S8B29E8TQVWNW4J";
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ConcurrentQueue<string> failures = new();
        private int exchanges;
        private int arrivals;
        private int publicReads;
        private readonly TaskCompletionSource overlap = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RequireOverlap { get; set; }
        public int PublicReads => publicReads;
        public string Fault { get; set; } = "";
        public int Exchanges => exchanges;
        public string LastToken { get; private set; } = "";
        public string LastScope { get; private set; } = "";
        public IReadOnlyList<string> Failures => failures.ToArray();

        public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (RequireOverlap)
            {
                if (request.RequestUri!.AbsolutePath.Contains("/.well-known/", StringComparison.Ordinal)) Interlocked.Increment(ref publicReads);
                if (Interlocked.Increment(ref arrivals) == 3) overlap.TrySetResult();
                await overlap.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/terrascale/.well-known/openid-configuration":
                    return Json(new
                    {
                        issuer = Origin,
                        authorization_endpoint = Origin + "/oauth/authorize",
                        token_endpoint = Origin + "/oauth/token",
                        userinfo_endpoint = Origin + "/oauth/userinfo",
                        jwks_uri = Origin + "/.well-known/jwks.json",
                        revocation_endpoint = Origin + "/oauth/revoke",
                        end_session_endpoint = Origin + "/oauth/logout",
                        response_types_supported = new[] { "code" },
                        grant_types_supported = new[] { "authorization_code", "refresh_token" },
                        code_challenge_methods_supported = new[] { "S256" },
                        subject_types_supported = new[] { "public" },
                        id_token_signing_alg_values_supported = new[] { "ES256" },
                        token_endpoint_auth_methods_supported = new[] { "client_secret_basic" },
                        scopes_supported = new[] { "openid", "email", "profile", "offline_access" }
                    });
                case "/terrascale/.well-known/jwks.json":
                    var point = key.ExportParameters(false).Q;
                    return Json(new { keys = new[] { new { kty = "EC", crv = "P-256", alg = "ES256", use = "sig", kid = "journey-key", x = B64(point.X!), y = B64(point.Y!) } } });
                case "/terrascale/oauth/token":
                    var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
                    return Exchange(request, form);
                default:
                    failures.Enqueue("Unexpected issuer call " + request.RequestUri.AbsolutePath);
                    return new(HttpStatusCode.NotFound);
            }
        }

        private HttpResponseMessage Exchange(HttpRequestMessage request, Dictionary<string, string> form)
        {
            Interlocked.Increment(ref exchanges);
            if (request.Headers.Authorization?.Scheme != "Basic" || form.GetValueOrDefault("grant_type") != "urn:ietf:params:oauth:grant-type:token-exchange" ||
                form.GetValueOrDefault("subject_token_type") != "urn:ietf:params:oauth:token-type:access_token")
            {
                failures.Enqueue("Exchange is not a client-authenticated RFC 8693 request.");
                return Error("invalid_request");
            }
            if (resolve(form.GetValueOrDefault("subject_token") ?? "") is not { } user) return Error("invalid_grant");
            LastScope = form.GetValueOrDefault("scope") ?? "";
            var now = DateTimeOffset.UtcNow;
            var claims = new Dictionary<string, object>
            {
                ["iss"] = Origin,
                ["aud"] = Fault == "wrong-audience" ? "https://gru.api.terrascale.tech" : "https://api.terrascale.tech",
                ["sub"] = user.UserId,
                ["client_id"] = ClientId,
                ["org_id"] = form["organization"],
                ["project_id"] = Fault == "foreign-project" ? new TerraScale.DotNet.TerraScaleIdGenerator().NewId() : form["project"],
                ["scope"] = LastScope,
                ["iat"] = now.ToUnixTimeSeconds(),
                ["exp"] = (Fault == "expired" ? now.AddMinutes(-1) : now.AddMinutes(10)).ToUnixTimeSeconds()
            };
            if (Fault == "extra-database") claims["database"] = "orders";
            var unsigned = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = "journey-key", typ = "at+jwt" })) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(claims));
            LastToken = unsigned + "." + B64(key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            return Json(new { access_token = LastToken, token_type = "Bearer", issued_token_type = "urn:ietf:params:oauth:token-type:access_token", expires_in = 600 });
        }

        private static HttpResponseMessage Error(string code) =>
            new(HttpStatusCode.BadRequest) { Content = new StringContent(JsonSerializer.Serialize(new { error = code }), Encoding.UTF8, new MediaTypeHeaderValue("application/json")) };
        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, new MediaTypeHeaderValue("application/json")) };
        private static string B64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
