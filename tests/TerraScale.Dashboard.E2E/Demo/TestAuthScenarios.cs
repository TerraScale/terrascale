using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Usage;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E;

// Test-only external producer. Dashboard requests still pass through the real
// TsAuthGateway, official SDK, signed identity/access tokens and persistent sessions.
public sealed class AuthProviderScenario : IAsyncDisposable
{
    private WebApplication? producer;
    private WebApplication? first;
    private WebApplication? second;
    private ProducerTransport? transport;
    private readonly ScenarioClock clock = new();
    private readonly TestState state = new();
    private readonly ScenarioProvider provider;
    private readonly ScenarioIdentity identity = new();
    private readonly DemoEnvironment environment = new();
    private readonly ConcurrentQueue<string> cliRequests = new();
    private string root = "";
    private bool liveProjects;

    private AuthProviderScenario() => provider = new(clock, state, environment.Workspace, cliRequests);

    public Uri Origin { get; private set; } = null!;
    public Uri ReplicaOrigin { get; private set; } = null!;
    public Uri ProviderOrigin { get; private set; } = null!;
    public IReadOnlyList<string> CliRequests => cliRequests.ToArray();

    // liveProjects sends project tenant management through the real TsAuthGateway to this producer, which does
    // not serve that API, as ts-auth before it ships; otherwise the demo host's in-memory tenant answers.
    public static async Task<AuthProviderScenario> StartAsync(string root, bool liveProjects = false, Func<HttpContext, Task<IResult?>>? projectExtension = null)
    {
        var scenario = new AuthProviderScenario { liveProjects = liveProjects };
        scenario.provider.ProjectExtension = projectExtension;
        try { await scenario.InitializeAsync(root); return scenario; }
        catch { await scenario.DisposeAsync(); throw; }
    }

    public static async Task<AuthProviderScenario> StartOwnershipAsync(string root)
    {
        var scenario = new AuthProviderScenario();
        scenario.provider.ComposeOwnership();
        try { await scenario.InitializeAsync(root); return scenario; }
        catch { await scenario.DisposeAsync(); throw; }
    }

    public static async Task<AuthProviderScenario> StartReportingAsync(string root, Func<HttpContext, Task<IResult?>> reportingExtension)
    {
        var scenario = new AuthProviderScenario();
        scenario.provider.ComposeOwnership();
        scenario.provider.ReportingExtension = reportingExtension;
        try { await scenario.InitializeAsync(root); return scenario; }
        catch { await scenario.DisposeAsync(); throw; }
    }

    public HttpClient CreateClient(bool replica = false) => Client(replica ? ReplicaOrigin : Origin);
    public HttpClient CreateProviderClient() => Client(ProviderOrigin);
    public Task WaitForUserInfoAsync() => provider.UserInfoPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));

    private static HttpClient Client(Uri origin) => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = TestHttps.Validate,
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer()
    })
    { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };

    private async Task InitializeAsync(string root)
    {
        this.root = root;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        producer = builder.Build();
        producer.MapMethods("/{**path}", ["GET", "POST", "PATCH", "PUT", "DELETE"], (Func<HttpContext, Task<IResult>>)provider.DispatchAsync);
        await producer.StartAsync();
        ProviderOrigin = Address(producer);
        transport = new ProducerTransport(ProviderOrigin, provider.Billing, provider.ApiKeys);
        await environment.InitializeAsync();
        first = environment.CreateTestHost(root, 0, "http://127.0.0.1", Configure);
        second = environment.CreateTestHost(root, 0, "http://127.0.0.1", Configure);
        await first.StartAsync();
        await second.StartAsync();
        Origin = Address(first);
        ReplicaOrigin = Address(second);
        RegisterProvider();
    }

    // Lets HttpJourney follow the dashboard's redirects to https://auth.terrascale.tech into this producer.
    private void RegisterProvider()
    {
        HttpJourney.AuthProviders[Origin.GetLeftPart(UriPartial.Authority)] = ProviderOrigin;
        HttpJourney.AuthProviders[ReplicaOrigin.GetLeftPart(UriPartial.Authority)] = ProviderOrigin;
    }

    // Test-only stand-in for a user deciding on ts-auth's hosted device verification page.
    public bool DecideDevice(string userCode, string email = "user@example.com", bool approve = true) => provider.DecideDevice(userCode, email, approve);

    private void Configure(WebApplicationBuilder application)
    {
        var stripe = (TestStripeTransport)application.Services.Last(descriptor => descriptor.ServiceType == typeof(TestStripeTransport)).ImplementationInstance!;
        transport!.UseStripe(stripe);
        application.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(provider.Billing));
        application.Services.Replace(ServiceDescriptor.Singleton<IDashboardIdentity>(identity));
        application.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        application.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new ProducerFactory(transport)));
        application.Services.AddSingleton<TsAuthGateway>();
        application.Services.Replace(ServiceDescriptor.Singleton<IAuthGateway>(services => services.GetRequiredService<TsAuthGateway>()));
        application.Services.Replace(ServiceDescriptor.Singleton<IAuthRefreshGateway>(services => services.GetRequiredService<TsAuthGateway>()));
        application.Services.Replace(ServiceDescriptor.Singleton<IAuthOidcGateway>(services => services.GetRequiredService<TsAuthGateway>()));
        application.Services.Replace(ServiceDescriptor.Singleton<IAuthLogoutGateway>(services => services.GetRequiredService<TsAuthGateway>()));
        application.Services.Replace(ServiceDescriptor.Singleton<IPlatformRoleGateway>(services => services.GetRequiredService<TsAuthGateway>()));
        application.Services.Replace(ServiceDescriptor.Singleton<IProjectAccessTokens>(services => services.GetRequiredService<TsAuthGateway>()));
        application.Services.Replace(ServiceDescriptor.Singleton<IUsageMeasurements, UsageBackend>());
        if (liveProjects) application.Services.Replace(ServiceDescriptor.Singleton<ITsAuthProjects>(services => services.GetRequiredService<TsAuthGateway>()));
        if (provider.ReportingExtension is not null)
            application.Services.Replace(ServiceDescriptor.Singleton<TerraScale.Dashboard.Features.Home.IHomeMeasurements, TerraScale.Dashboard.Features.Home.TerraBaseHomeMeasurements>());
        provider.Ownership?.Configure(application);
    }

    // Invoke the production command entry and complete graph, with only its
    // named HTTP transport pointed at the already-running native application.
    public Task<int> RunCliAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default) =>
        InternalBillingCommands.RunAsync(args, _ =>
        {
            provider.Billing.BeforeCliBuild();
            return environment.CreateTestHost(root, 0, Origin.ToString().TrimEnd('/'), Configure);
        }, output, error, cancellationToken);

    public async Task RestartAsync()
    {
        await second!.StopAsync(); await second.DisposeAsync();
        await first!.StopAsync(); await first.DisposeAsync();
        first = environment.CreateTestHost(root, 0, "http://127.0.0.1", Configure);
        second = environment.CreateTestHost(root, 0, "http://127.0.0.1", Configure);
        await first.StartAsync(); await second.StartAsync();
        Origin = Address(first); ReplicaOrigin = Address(second);
        RegisterProvider();
    }

    private static Uri Address(WebApplication application) => new(application.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        provider.ReleaseRefresh();
        provider.ReleaseUserInfo();
        provider.ReleaseAccountReservations();
        provider.Billing.Release();
        if (second is not null) { await second.StopAsync(); await second.DisposeAsync(); second = null; }
        if (first is not null) { await first.StopAsync(); await first.DisposeAsync(); first = null; }
        if (producer is not null) { await producer.StopAsync(); await producer.DisposeAsync(); producer = null; }
        transport?.Dispose();
        provider.Dispose();
    }

    private sealed class ScenarioIdentity : IDashboardIdentity
    {
        private readonly DashboardCredentials identity = new("test-dashboard-client-secret-0123456789abcdef",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), new("sk_test_demo", "whsec_demo"), TestIdentity.PeerSecret);
        public Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(identity);
    }

    private sealed class ScenarioClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(int seconds) => Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
    }

    private sealed class ProducerFactory(ProducerTransport transport) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(transport, disposeHandler: false);
    }

    private sealed class ProducerTransport(Uri localOrigin, InternalBillingProducer billing, TestAuthApiKeys apiKeys) : DelegatingHandler(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false })
    {
        private HttpMessageInvoker? stripe;
        private TestStripeTransport? stripeHandler;
        public void UseStripe(TestStripeTransport handler)
        {
            if (ReferenceEquals(stripeHandler, handler)) return;
            stripe?.Dispose();
            stripeHandler = handler;
            stripe = new(handler, disposeHandler: false);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var original = request.RequestUri!;
            if (original.Scheme == "https" && original.IsDefaultPort && original.UserInfo.Length == 0 && original.Host == "api.stripe.com")
            {
                billing.ObserveStripe(request);
                return stripe!.SendAsync(request, cancellationToken);
            }
            if (original.Scheme != "https" || !original.IsDefaultPort || original.UserInfo.Length != 0 ||
                original.Host is not ("auth.terrascale.tech" or "api.terrascale.tech" or "gru.api.terrascale.tech"))
                throw new HttpRequestException("Unexpected outbound origin in the test-only auth producer.");
            if (original.Host == "auth.terrascale.tech" && original.AbsolutePath.StartsWith("/v1/terrascale/organizations/", StringComparison.Ordinal) &&
                original.AbsolutePath.Contains("/api-keys", StringComparison.Ordinal)) return apiKeys.SendAsync(request, cancellationToken);
            // Preserve the trusted Host and all authorization headers. Only the
            // physical test transport changes; issuer/audience/signatures do not.
            request.Headers.Host = original.Authority;
            request.RequestUri = new(localOrigin, original.PathAndQuery);
            return base.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) stripe?.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ScenarioProvider(ScenarioClock clock, TestState state, TestWorkspace workspace, ConcurrentQueue<string> cliRequests) : IDisposable
    {
        // ts-auth 6bb896bb: one global issuer, {PublicOrigin}/{tenant}, served from every region.
        private const string Issuer = "https://auth.terrascale.tech/terrascale";
        private const string ClientId = "01M4EQ2FRJ8S8B29E8TQVWNW4J";
        private readonly ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly object keyGate = new();
        private readonly string subject = new TerraScaleIdGenerator().NewId();
        private readonly Principal foreignPrincipal = new(new TerraScaleIdGenerator().NewId(), "foreign@example.com", "Foreign Protocol User", "", 0, null);
        private readonly ConcurrentDictionary<string, Principal> principals = new(StringComparer.Ordinal);
        private long authEpoch;
        private InternalBillingProducer? billing;
        private TestAuthApiKeys? apiKeys;
        public TestAuthApiKeys ApiKeys => apiKeys ??= new(ResolveActiveBearer, workspace.ProjectRole);
        public InternalBillingProducer Billing => billing ??= new(state, workspace, clock.GetUtcNow, () => subject, organization => Ownership?.SeedOrganization(organization));
        public OwnershipSdkProtocol? Ownership { get; private set; }
        public Func<HttpContext, Task<IResult?>>? ReportingExtension { get; set; }
        public Func<HttpContext, Task<IResult?>>? ProjectExtension { get; set; }
        public void ComposeOwnership() => Ownership = new(workspace, ResolveActiveBearer);
        private readonly ConcurrentDictionary<string, Grant> access = new();
        private readonly ConcurrentDictionary<string, Grant> refresh = new();
        private readonly ConcurrentDictionary<string, ProjectGrant> projectGrants = new();
        private readonly ConcurrentQueue<object> projectScopeExchanges = new();
        private readonly ConcurrentQueue<object> projectRequests = new();
        private int projectExchanges;
        private int projectReportReads;
        private readonly ConcurrentDictionary<string, Authorization> codes = new();
        private readonly ConcurrentDictionary<string, byte> consumedRefresh = new();
        private TaskCompletionSource? refreshGate;
        private int refreshesBeforePause;
        private TaskCompletionSource? userInfoGate;
        public TaskCompletionSource UserInfoPaused { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int pauseNextUserInfo;
        private int userinfoPauses;
        private int descendantIssuerGrants;
        private string fault = "";
        private string CurrentIssuer => fault == "descendant-issuer" ? Issuer + "/descendant" : Issuer;
        private string? platformRole;
        private string password = "test-password";
        private int tokenSeconds = 3600;
        private int refreshes;
        private int refreshPauses;
        private int codeExchanges;
        private int managementRequests;
        private int projectManagementRequests;
        private int managementMutations;
        private int revocations;
        private int contractFailures;
        private readonly ConcurrentQueue<string> contractFailureMessages = new();
        private int userinfoRequests;
        private int unsupportedIntrospectionRequests;
        private int forgedIdRoleTokens;
        private bool dropRefresh;
        private bool unavailableRefresh;
        private bool revoked;
        private bool storedClaimSpoof;
        private TaskCompletionSource? accountReservations;
        private int identityAttempts;
        private int projectionWrites;

        public async Task<IResult> DispatchAsync(HttpContext http)
        {
            var path = http.Request.Path.Value!;
            if (path == "/__test/control") return await ControlAsync(http);
            if (path == "/__test/observations") return Observations();
            if (path == "/__test/ownership-sdk/probe" && Ownership is not null)
                return await Ownership.DispatchAsync(http) ?? Results.NotFound();
            // terrascale e30ffdc2: api.terrascale.tech answers in the nearest region and <region>.api.terrascale.tech in its
            // own; every answer names its region. There is no Atlas region catalog.
            if (http.Request.Host.Host is "api.terrascale.tech" or "gru.api.terrascale.tech")
            {
                http.Response.Headers["X-TerraBase-Region"] = "gru";
                if (path == "/healthz")
                    return Results.Json(new { status = "ok", version = "test", region = "gru", storageFormat = "test", revision = (string?)null, machine = "local" });
                if (ReportingExtension is not null && path.StartsWith("/v1/projects/", StringComparison.Ordinal) &&
                    (path.EndsWith("/analytics", StringComparison.Ordinal) || path.EndsWith("/replication", StringComparison.Ordinal)))
                {
                    var projectId = path.Split('/')[3];
                    if (!ProjectBearer(http, projectId, out var projectGrant)) return OAuthError("unauthorized", 401);
                    Interlocked.Increment(ref projectReportReads);
                    projectRequests.Enqueue(new
                    {
                        path,
                        query = http.Request.QueryString.Value,
                        projectGrant.OrganizationId,
                        projectGrant.ProjectId,
                        subject = projectGrant.Session.Subject,
                        audience = "https://api.terrascale.tech",
                        scope = "databases:read",
                        tokenClass = "project"
                    });
                    return await ReportingExtension(http) ?? Results.NotFound();
                }
                if (http.Request.Method == "GET" && path.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["v1", "projects", var usageProject, "usage"])
                {
                    if (!ProjectBearer(http, usageProject, out var usageGrant)) return OAuthError("unauthorized", 401);
                    return await Billing.UsageAsync(http, usageGrant.Session.Subject, usageGrant.OrganizationId, usageProject, usageGrant.Scopes);
                }
                return Results.NotFound();
            }
            if (http.Request.Host.Host != "auth.terrascale.tech")
            {
                // The browser-equivalent authorize request uses the producer's
                // physical URL and still supplies its public trusted Host.
                return ContractFailure("The physical transport did not preserve the trusted auth origin.");
            }
            if (Ownership is not null && path.StartsWith("/v1/terrascale/", StringComparison.Ordinal))
                return await Ownership.DispatchAsync(http) ?? Results.NotFound();
            if (path == "/terrascale/.well-known/openid-configuration") return Discovery();
            if (path == "/terrascale/.well-known/jwks.json")
            {
                ECParameters parameters;
                lock (keyGate) parameters = (fault == "jwks-substitution" ? wrongKey : signingKey).ExportParameters(false);
                return Results.Json(new { keys = new[] { new { kty = "EC", crv = "P-256", alg = "ES256", use = "sig", kid = "test-signing-key", x = B64(parameters.Q.X!), y = B64(parameters.Q.Y!) } } });
            }
            // A descendant-issuer discovery points the browser below the tenant; the hosted stand-in serves it too.
            if (path is "/terrascale/oauth/authorize" or "/terrascale/descendant/oauth/authorize")
            {
                var query = http.Request.Query;
                if (query["client_id"] != ClientId || query["response_type"] != "code" || query["code_challenge_method"] != "S256" ||
                    query["scope"] != "openid email profile offline_access" || query["nonce"].ToString().Length != 64 ||
                    query["state"].ToString().Length != 64 || query["code_challenge"].ToString().Length != 43 ||
                    !Uri.TryCreate(query["redirect_uri"], UriKind.Absolute, out var redirect) || redirect.Host != "127.0.0.1" || redirect.AbsolutePath != "/login/callback")
                    return ContractFailure("Invalid native SSO authorization request.");
                // ts-auth AuthorizeEndpoint: prompt=login, consent and select_account are unsupported and answer the client.
                foreach (var (prompt, error) in new[] { ("login", "login_required"), ("consent", "consent_required"), ("select_account", "account_selection_required") })
                    if (query["prompt"].ToString().Split(' ').Contains(prompt))
                        return Results.Redirect(redirect.AbsoluteUri + "?error=" + error + "&state=" + Uri.EscapeDataString(query["state"].ToString()));
                // Test-only stand-in for the hosted sign-in/sign-up page: the journey names the account.
                var hostedEmail = query["email"].Count == 0 ? "user@example.com" : query["email"].ToString().Trim().ToLowerInvariant();
                if (query["signup"] == "1" && hostedEmail != "user@example.com")
                    principals.TryAdd(hostedEmail, new(new TerraScaleIdGenerator().NewId(), hostedEmail, hostedEmail, query["password"].ToString(), 0, null));
                var who = hostedEmail == "user@example.com" ? DefaultPrincipal : principals.GetValueOrDefault(hostedEmail);
                if (who is null || query["password"].Count != 0 && query["password"] != who.Password)
                    return Results.Redirect(redirect.AbsoluteUri + "?error=access_denied&state=" + Uri.EscapeDataString(query["state"].ToString()));
                var code = "authorization-" + Guid.NewGuid().ToString("N");
                codes[code] = new(query["nonce"].ToString(), query["code_challenge"].ToString(), redirect.AbsoluteUri, hostedEmail);
                return Results.Redirect(redirect.AbsoluteUri + "?state=" + Uri.EscapeDataString(query["state"].ToString()) + "&code=" + code);
            }
            if (path == "/terrascale/oauth/userinfo")
            {
                Interlocked.Increment(ref userinfoRequests);
                if (fault == "userinfo-outage") return OAuthError("temporarily_unavailable", 503);
                if (Interlocked.Exchange(ref pauseNextUserInfo, 0) == 1 && userInfoGate is { } gate)
                {
                    Interlocked.Increment(ref userinfoPauses);
                    UserInfoPaused.TrySetResult();
                    await gate.Task.WaitAsync(http.RequestAborted);
                }
                if (!Bearer(http, out var grant)) return OAuthError("invalid_token", 401);
                // The current public contract exposes no name or platform role.
                return Results.Json(new
                {
                    sub = fault == "userinfo-substitution" ? "foreign-subject" : grant.Subject,
                    email = fault == "userinfo-email-substitution" ? "foreign@example.com" : PrivatePrincipal(grant.Subject)!.Email,
                    email_verified = fault != "unverified-userinfo"
                });
            }
            if (path == "/terrascale/oauth/introspect")
            {
                Interlocked.Increment(ref unsupportedIntrospectionRequests);
                return Results.NotFound();
            }
            if (path.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["v1", var tenant, ..] && TerraScaleIds.IsValid(tenant))
            {
                Interlocked.Increment(ref projectManagementRequests);
                if (ProjectExtension is not null)
                    return Bearer(http, out _) ? await ProjectExtension(http) ?? Results.NotFound() : OAuthError("invalid_token", 401);
                return OAuthError("not_found", 404);
            }
            if (path.StartsWith("/v1/terrascale/users/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref managementRequests);
                if (http.Request.Method is not "GET" and not "HEAD") Interlocked.Increment(ref managementMutations);
                return Results.NotFound();
            }
            if (http.Request.Method == "GET" && path == "/v1/terrascale/account/providers")
                return Bearer(http, out _) ? Results.Json(new { providers = Array.Empty<string>() }) : OAuthError("invalid_token", 401);
            // This producer predates the two-factor routes; like such a ts-auth it answers 404, and the dashboard leaves the status unknown.
            if (http.Request.Method == "GET" && (path == "/v1/terrascale/account/mfa" || path.StartsWith("/v1/terrascale/organizations/", StringComparison.Ordinal)))
                return Results.NotFound();
            if (!http.Request.HasFormContentType) return ContractFailure("Missing form-encoded auth operation.");
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (form["client_id"] == "00000000000000000000000007" && !http.Request.Headers.ContainsKey("Authorization") && form["client_secret"].Count == 0)
                return await CliAsync(http, path, form);
            if (!Confidential(http, form)) return ContractFailure("Missing confidential client credentials.");
            if (path == "/terrascale/oauth/revoke")
            {
                var found = refresh.GetValueOrDefault(form["token"].ToString()) ?? access.GetValueOrDefault(form["token"].ToString());
                if (found is not null)
                    foreach (var entry in access.Values.Where(item => item.Family == found.Family)) entry.Revoked = true;
                Interlocked.Increment(ref revocations);
                return Results.Ok(new { });
            }
            if (path != "/terrascale/oauth/token") return ContractFailure("Unknown auth producer operation.");
            if (fault == "upstream-rate-limit") { http.Response.Headers.RetryAfter = "60"; return OAuthError("rate_limited", 429); }
            if (fault == "invalid-client") return OAuthError("invalid_client", 401);
            var kind = form["grant_type"].ToString();
            if (kind == "urn:ietf:params:oauth:grant-type:token-exchange") return ProjectExchange(form);
            if (kind == "refresh_token")
            {
                if (unavailableRefresh) { unavailableRefresh = false; return OAuthError("temporarily_unavailable", 503); }
                Interlocked.Increment(ref refreshes);
                var secret = form["refresh_token"].ToString();
                if (!refresh.TryGetValue(secret, out var previous) || previous.Revoked || revoked || !consumedRefresh.TryAdd(secret, 0))
                    return OAuthError("invalid_grant", 400);
                var principal = PrivatePrincipal(previous.Subject);
                if (principal is null || principal.AuthEpoch != previous.AuthEpoch) return OAuthError("invalid_grant", 400);
                var answer = Issue(previous.Family, includeIdToken: false, previousAccess: previous.AccessToken, principal: principal);
                if (refreshGate is { } gate && Interlocked.Decrement(ref refreshesBeforePause) < 0)
                {
                    Interlocked.Increment(ref refreshPauses);
                    await gate.Task.WaitAsync(http.RequestAborted);
                }
                if (dropRefresh) { http.Abort(); return Results.Empty; }
                return Results.Json(answer);
            }
            if (kind == "authorization_code")
            {
                Interlocked.Increment(ref codeExchanges);
                if (!codes.TryRemove(form["code"].ToString(), out var authorization) ||
                    authorization.RedirectUri != form["redirect_uri"] ||
                    authorization.Challenge != B64(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))))
                    return ContractFailure("Authorization code did not bind the original PKCE/redirect URI.");
                var signedIn = authorization.Email == "user@example.com" ? DefaultPrincipal : principals.GetValueOrDefault(authorization.Email);
                if (signedIn is null) return OAuthError("invalid_grant", 400);
                return Results.Json(Issue(nonce: authorization.Nonce, principal: signedIn));
            }
            return ContractFailure("Unsupported token grant.");
        }

        // ts-auth's public CLI client: RFC 8628 device authorization, its token polling, refresh and revocation.
        private readonly ConcurrentDictionary<string, DeviceAuthorization> devices = new();
        public bool DecideDevice(string userCode, string email, bool approve)
        {
            var entry = devices.FirstOrDefault(item => item.Value.UserCode == userCode);
            if (entry.Key is null || entry.Value.Email is not null || entry.Value.Denied) return false;
            return devices.TryUpdate(entry.Key, entry.Value with { Email = approve ? email : null, Denied = !approve }, entry.Value);
        }

        private async Task<IResult> CliAsync(HttpContext http, string path, IFormCollection form)
        {
            cliRequests.Enqueue(http.Request.Method + " " + path);
            if (path == "/terrascale/oauth/device")
            {
                if (form["scope"] != "openid email offline_access") return ContractFailure("Device authorization did not request the CLI scope.");
                var device = B64(RandomNumberGenerator.GetBytes(32));
                var raw = RandomNumberGenerator.GetString("BCDFGHJKLMNPQRSTVWXZ", 8);
                var userCode = raw[..4] + "-" + raw[4..];
                devices[device] = new(userCode, clock.GetUtcNow().AddMinutes(10));
                return Results.Json(new
                {
                    device_code = device,
                    user_code = userCode,
                    verification_uri = "https://auth.terrascale.tech/ui/terrascale/device",
                    verification_uri_complete = "https://auth.terrascale.tech/ui/terrascale/device?user_code=" + Uri.EscapeDataString(userCode),
                    expires_in = 600,
                    interval = 1
                });
            }
            if (path == "/terrascale/oauth/revoke")
            {
                var found = refresh.GetValueOrDefault(form["token"].ToString()) ?? access.GetValueOrDefault(form["token"].ToString());
                if (found is not null)
                    foreach (var entry in access.Values.Where(item => item.Family == found.Family)) entry.Revoked = true;
                Interlocked.Increment(ref revocations);
                return Results.Ok(new { });
            }
            if (path != "/terrascale/oauth/token") return ContractFailure("Unknown CLI auth operation.");
            if (form["grant_type"] == "urn:ietf:params:oauth:grant-type:device_code")
            {
                if (!devices.TryGetValue(form["device_code"].ToString(), out var pending)) return OAuthError("invalid_grant", 400);
                if (pending.ExpiresAt <= clock.GetUtcNow()) { devices.TryRemove(form["device_code"].ToString(), out _); return OAuthError("expired_token", 400); }
                if (pending.Denied) { devices.TryRemove(form["device_code"].ToString(), out _); return OAuthError("access_denied", 400); }
                if (pending.Email is null) return OAuthError("authorization_pending", 400);
                if (!devices.TryRemove(form["device_code"].ToString(), out _)) return OAuthError("invalid_grant", 400);
                var approver = pending.Email == "user@example.com" ? DefaultPrincipal : principals.GetValueOrDefault(pending.Email);
                return approver is null ? OAuthError("invalid_grant", 400) : Results.Json(Issue(includeIdToken: false, principal: approver));
            }
            if (form["grant_type"] == "refresh_token")
            {
                Interlocked.Increment(ref refreshes);
                var secret = form["refresh_token"].ToString();
                if (!refresh.TryGetValue(secret, out var previous) || previous.Revoked || revoked || !consumedRefresh.TryAdd(secret, 0))
                    return OAuthError("invalid_grant", 400);
                var principal = PrivatePrincipal(previous.Subject);
                if (principal is null || principal.AuthEpoch != previous.AuthEpoch) return OAuthError("invalid_grant", 400);
                var answer = Issue(previous.Family, includeIdToken: false, previousAccess: previous.AccessToken, principal: principal);
                // The same pause and lost-reply controls as the confidential client's rotation.
                if (refreshGate is { } gate && Interlocked.Decrement(ref refreshesBeforePause) < 0)
                {
                    Interlocked.Increment(ref refreshPauses);
                    await gate.Task.WaitAsync(http.RequestAborted);
                }
                if (dropRefresh) { http.Abort(); return Results.Empty; }
                return Results.Json(answer);
            }
            return OAuthError("unsupported_grant_type", 400);
        }

        private IResult ProjectExchange(IFormCollection form)
        {
            Interlocked.Increment(ref projectExchanges);
            var requested = form["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (form["subject_token_type"] != "urn:ietf:params:oauth:token-type:access_token" || requested.Length == 0 ||
                requested.Distinct(StringComparer.Ordinal).Count() != requested.Length)
                return ContractFailure("Project exchange did not request its exact subject type and distinct action scopes.");
            if (!ActiveBearer(form["subject_token"].ToString(), out var session)) return OAuthError("invalid_grant", 400);
            var organizationId = form["organization"].ToString();
            var projectId = form["project"].ToString();
            var role = workspace.ProjectRole(session.Subject, organizationId, projectId);
            var allowed = role == "viewer" ? new[] { "databases:read", "documents:read" }
                : role is "member" or "admin" ? new[] { "databases:read", "databases:write", "documents:read", "documents:write" } : [];
            var error = !TerraScaleIds.IsValid(organizationId) || !TerraScaleIds.IsValid(projectId) || allowed.Length == 0 || fault == "project-refused"
                ? "invalid_target" : requested.Except(allowed, StringComparer.Ordinal).Any() ? "invalid_scope" : "";
            projectScopeExchanges.Enqueue(new { organizationId, projectId, requested, granted = error.Length == 0 ? requested : [], error });
            if (error.Length > 0) return OAuthError(error, 400);
            var expires = clock.GetUtcNow().AddMinutes(15);
            if (expires > session.ExpiresAt) expires = session.ExpiresAt;
            var claims = new Dictionary<string, object>
            {
                ["iss"] = CurrentIssuer,
                // ts-auth PlatformGrants: organization and project tokens carry AccessTokenAudiences, TerraBase's global audience.
                ["aud"] = fault == "project-wrong-audience" ? "foreign-resource" : "https://api.terrascale.tech",
                ["sub"] = fault == "project-wrong-subject" ? foreignPrincipal.Subject : session.Subject,
                ["client_id"] = fault == "project-wrong-client" ? "foreign-client" : ClientId,
                ["org_id"] = fault == "project-foreign-organization" ? new TerraScaleIdGenerator().NewId() : organizationId,
                ["project_id"] = fault == "project-foreign-project" ? new TerraScaleIdGenerator().NewId() : projectId,
                ["scope"] = fault == "project-missing-scope" ? "" : string.Join(' ', requested),
                ["auth_time"] = session.AuthenticatedAt,
                ["iat"] = clock.GetUtcNow().ToUnixTimeSeconds(),
                ["exp"] = (fault == "project-expired" ? clock.GetUtcNow().AddMinutes(-1) : fault == "project-too-long" ? clock.GetUtcNow().AddHours(1) : expires).ToUnixTimeSeconds(),
                ["jti"] = Guid.NewGuid().ToString("N")
            };
            var header = new { alg = "ES256", kid = "test-signing-key", typ = fault == "project-identity-token" ? "JWT" : "at+jwt" };
            var unsigned = B64(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(claims));
            byte[] signature;
            lock (keyGate) signature = (fault == "project-bad-signature" ? wrongKey : signingKey).SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var token = unsigned + "." + B64(signature);
            projectGrants[token] = new(session, organizationId, projectId, expires, requested);
            var response = new Dictionary<string, object>
            {
                ["access_token"] = token,
                ["token_type"] = "Bearer",
                ["issued_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["expires_in"] = (int)(expires - clock.GetUtcNow()).TotalSeconds
            };
            // OpenIddict 7.7.1 (OpenIddictServerHandlers.cs:5670): a token response names scope only when the granted
            // scopes differ from the requested ones, and this exchange grants exactly what it requested.
            if (fault == "project-response-scope") response["scope"] = "databases:read databases:write";
            if (fault == "project-refreshtoken") response["refresh_token"] = "unexpected-private-refresh-value";
            if (fault == "project-idtoken") response["id_token"] = "unexpected-private-identity-value";
            return Results.Json(response);
        }

        private bool ProjectBearer(HttpContext http, string projectId, out ProjectGrant grant)
        {
            var header = http.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
            grant = projectGrants.GetValueOrDefault(token)!;
            if (grant is null || grant.ProjectId != projectId || grant.ExpiresAt <= clock.GetUtcNow() || !ActiveBearer(grant.Session.AccessToken, out _)) return false;
            try
            {
                var parts = token.Split('.');
                using var jwtHeader = JsonDocument.Parse(UnB64(parts[0]));
                using var payload = JsonDocument.Parse(UnB64(parts[1]));
                var claims = payload.RootElement;
                if (jwtHeader.RootElement.GetProperty("typ").GetString() != "at+jwt" || claims.GetProperty("iss").GetString() != CurrentIssuer ||
                    claims.GetProperty("aud").GetString() != "https://api.terrascale.tech" || !grant.Scopes.Contains("databases:read") ||
                    claims.GetProperty("scope").GetString() != string.Join(' ', grant.Scopes) ||
                    claims.GetProperty("sub").GetString() != grant.Session.Subject || claims.GetProperty("client_id").GetString() != ClientId ||
                    claims.GetProperty("org_id").GetString() != grant.OrganizationId || claims.GetProperty("project_id").GetString() != grant.ProjectId ||
                    claims.GetProperty("exp").GetInt64() <= clock.GetUtcNow().ToUnixTimeSeconds()) return false;
                lock (keyGate) return signingKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), UnB64(parts[2]),
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or CryptographicException)
            { return false; }
        }

        private IResult Discovery() => Results.Json(new
        {
            issuer = CurrentIssuer,
            authorization_endpoint = (fault == "untrusted-endpoint" ? "https://untrusted.example/terrascale" : CurrentIssuer) + "/oauth/authorize",
            token_endpoint = CurrentIssuer + "/oauth/token",
            userinfo_endpoint = CurrentIssuer + "/oauth/userinfo",
            jwks_uri = CurrentIssuer + "/.well-known/jwks.json",
            revocation_endpoint = CurrentIssuer + "/oauth/revoke",
            end_session_endpoint = CurrentIssuer + "/oauth/logout",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "ES256" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" },
            scopes_supported = new[] { "openid", "email", "profile", "offline_access" }
        });

        private Principal DefaultPrincipal => new(subject, "user@example.com", "Protocol User", password, authEpoch, platformRole);
        private Principal? PrivatePrincipal(string userId) => userId == subject ? DefaultPrincipal : userId == foreignPrincipal.Subject
            ? foreignPrincipal : principals.Values.FirstOrDefault(principal => principal.Subject == userId);

        private object Issue(string? family = null, bool includeIdToken = true, string? nonce = null, string? previousAccess = null, Principal? principal = null)
        {
            principal ??= DefaultPrincipal;
            var seconds = family is null ? tokenSeconds : 3600;
            var grant = new Grant(principal.Subject, principal.Email, principal.AuthEpoch, family ?? Guid.NewGuid().ToString("N"), clock.GetUtcNow().AddSeconds(seconds), principal.PlatformRole);
            grant.AuthenticatedAt = previousAccess is not null && access.TryGetValue(previousAccess, out var original) ? original.AuthenticatedAt : clock.GetUtcNow().ToUnixTimeSeconds();
            if (fault == "descendant-issuer") Interlocked.Increment(ref descendantIssuerGrants);
            var accessToken = fault == "repeat-access" && previousAccess is not null ? previousAccess : AccessToken(grant);
            grant.AccessToken = accessToken;
            var refreshToken = "protocol-refresh-" + Guid.NewGuid().ToString("N");
            access[accessToken] = grant;
            refresh[refreshToken] = grant;
            return new
            {
                access_token = accessToken,
                refresh_token = fault == "no-refresh" ? null : refreshToken,
                token_type = "Bearer",
                expires_in = seconds,
                id_token = includeIdToken ? IdToken(nonce, principal) : null,
                scope = "openid email profile offline_access"
            };
        }

        private string IdToken(string? nonce, Principal principal)
        {
            var claims = new Dictionary<string, object>
            {
                ["iss"] = fault == "wrong-issuer" ? "https://untrusted.example/terrascale" : CurrentIssuer,
                ["aud"] = fault == "wrong-audience" ? "foreign-client" : ClientId,
                ["sub"] = fault == "id-substitution" ? "foreign-subject" : principal.Subject,
                ["email"] = principal.Email,
                ["name"] = principal.Name,
                ["email_verified"] = fault != "unverified-id",
                ["exp"] = (fault == "expired-id" ? clock.GetUtcNow().AddMinutes(-10) : clock.GetUtcNow().AddMinutes(10)).ToUnixTimeSeconds()
            };
            if (nonce is not null) claims["nonce"] = fault == "wrong-nonce" ? "foreign-nonce" : nonce;
            if (fault == "forged-id-admin")
            {
                claims["platform_role"] = "admin";
                Interlocked.Increment(ref forgedIdRoleTokens);
            }
            var unsigned = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = "test-signing-key" })) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(claims));
            byte[] signature;
            lock (keyGate) signature = (fault == "bad-signature" ? wrongKey : signingKey).SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return unsigned + "." + B64(signature);
        }

        private string AccessToken(Grant grant)
        {
            var claims = new Dictionary<string, object>
            {
                ["iss"] = fault == "access-wrong-issuer" ? "https://untrusted.example/terrascale" : CurrentIssuer,
                // ts-auth OidcIssuer.SignInAudiences: the issuer, then TerraBase's global audience.
                ["aud"] = fault == "access-wrong-audience" ? "foreign-resource" : new[] { CurrentIssuer, "https://api.terrascale.tech" },
                ["sub"] = grant.Subject,
                ["email"] = grant.Email,
                ["email_verified"] = true,
                ["ts_auth_epoch"] = grant.AuthEpoch.ToString(CultureInfo.InvariantCulture),
                ["client_id"] = ClientId,
                ["auth_time"] = grant.AuthenticatedAt,
                ["scope"] = "openid email profile offline_access",
                ["iat"] = clock.GetUtcNow().ToUnixTimeSeconds(),
                ["exp"] = (fault == "access-expired" ? clock.GetUtcNow().AddMinutes(-10) : grant.ExpiresAt).ToUnixTimeSeconds(),
                ["jti"] = Guid.NewGuid().ToString("N")
            };
            if (grant.PlatformRole == "admin") claims["platform_role"] = "admin";
            if (fault == "access-role-type") claims["platform_role"] = true;
            var header = new { alg = "ES256", kid = "test-signing-key", typ = fault == "access-identity-token" ? "JWT" : "at+jwt" };
            var unsigned = B64(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(claims));
            byte[] signature;
            lock (keyGate) signature = (fault == "access-bad-signature" ? wrongKey : signingKey).SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return unsigned + "." + B64(signature);
        }

        private async Task<IResult> ControlAsync(HttpContext http)
        {
            if (http.Request.Method != "POST") return Results.BadRequest();
            using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            var body = json.RootElement;
            if (body.GetProperty("command").GetString()?.StartsWith("internal-", StringComparison.Ordinal) == true)
                return await Billing.ControlAsync(body, http.RequestAborted);
            switch (body.GetProperty("command").GetString())
            {
                case "advance": clock.Advance(body.GetProperty("seconds").GetInt32()); break;
                case "set-time": clock.Set(body.GetProperty("value").GetDateTimeOffset()); break;
                case "short-grants": tokenSeconds = 1; break;
                case "pause-refresh":
                    Interlocked.Exchange(ref refreshesBeforePause, 0);
                    refreshGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                case "pause-refresh-after":
                    if (!body.GetProperty("count").TryGetInt32(out var beforePause) || beforePause is < 1 or > 8) return Results.BadRequest();
                    Interlocked.Exchange(ref refreshesBeforePause, beforePause);
                    refreshGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                case "release-refresh": ReleaseRefresh(); break;
                case "pause-next-userinfo":
                    UserInfoPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    userInfoGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    Interlocked.Exchange(ref pauseNextUserInfo, 1);
                    break;
                case "release-userinfo": ReleaseUserInfo(); break;
                case "drop-refresh-response": dropRefresh = true; break;
                case "unavailable-refresh": unavailableRefresh = true; break;
                case "revoke": revoked = true; break;
                // Test-only stand-in for a password change on ts-auth's hosted account page: it revokes every grant of that user.
                case "change-password":
                    password = body.GetProperty("value").GetString()!;
                    authEpoch++;
                    foreach (var grant in access.Values.Where(grant => grant.Email == "user@example.com")) grant.Revoked = true;
                    break;
                case "expire-access":
                    foreach (var grant in access.Values) grant.ActivityExpiresAt = clock.GetUtcNow().AddSeconds(-1);
                    break;
                case "fault": fault = body.GetProperty("value").GetString()!; break;
                case "platform-role":
                    var value = body.GetProperty("value").GetString();
                    if (value is not ("" or "admin")) return Results.BadRequest();
                    platformRole = value.Length == 0 ? null : value;
                    break;
                case "seed-account": return await SeedAccountAsync(body.GetProperty("accountId").GetString()!);
                case "workspace-role":
                    var role = body.GetProperty("role").GetString()!;
                    if (role is not ("owner" or "admin" or "member" or "viewer" or "readonly" or "")) return Results.BadRequest();
                    workspace.Role = role;
                    lock (workspace.Gate)
                        foreach (var org in workspace.Organizations.Values.Where(org => org.Members.ContainsKey(subject)))
                        {
                            org.Members[subject].Role = role;
                            org.Members[subject].Status = role.Length == 0 ? "inactive" : "active";
                        }
                    break;
                case "account-corruption": CorruptAccount(body.GetProperty("value").GetString()!); break;
                case "pause-account-reservations":
                    var reservations = accountReservations = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    state.BeforeWriteAsync = async (collection, _) =>
                    {
                        if (collection != AccountService.IdentitiesCollection) return;
                        Interlocked.Increment(ref identityAttempts);
                        await reservations.Task;
                    };
                    break;
                case "release-account-reservations": accountReservations?.TrySetResult(); break;
                case "fail-account-projection-once":
                    state.WriteFailure = (collection, id) => collection == AccountService.AccountsCollection &&
                        !state.Items.ContainsKey(collection + "/" + id) &&
                        Interlocked.Increment(ref projectionWrites) == 1 ? new IOException("Injected test-only interrupted account projection.") : null;
                    break;
                case "spoof-stored-admin":
                    foreach (var entry in state.Items.Where(entry => entry.Key.StartsWith("dashboard_accounts/", StringComparison.Ordinal)))
                    {
                        var document = JsonSerializer.Deserialize<Dictionary<string, object>>(entry.Value.Document.GetRawText())!;
                        document["PlatformAdmin"] = true;
                        document["platform_admin"] = true;
                        state.Items[entry.Key] = entry.Value with { Document = JsonSerializer.SerializeToElement(document) };
                    }
                    storedClaimSpoof = true;
                    break;
                default: return Results.BadRequest();
            }
            return Results.Ok(new { configured = true });
        }

        private async Task<IResult> SeedAccountAsync(string publicId)
        {
            var key = AccountService.AccountStorageKey(subject);
            if (publicId == "legacy") publicId = key;
            else TerraScaleIds.Validate(publicId);
            var session = new SessionRecord(subject, "Protocol User", "user@example.com", "", null, clock.GetUtcNow().AddHours(1));
            var organization = workspace.SeedPersonal(session);
            var account = new AccountRecord
            {
                Id = publicId,
                UserId = subject,
                Name = session.Name,
                Email = session.Email,
                CreatedAt = clock.GetUtcNow(),
                OnboardingCompleted = true,
                OnboardingStep = "complete",
                OrganizationIds = [organization.Id],
                PersonalOrganizationId = organization.Id,
                ActiveOrganizationId = organization.Id,
                ActiveProjectId = organization.Projects.Keys.Single()
            };
            await state.PutAsync(AccountService.OrganizationsCollection, organization.Id, JsonSerializer.SerializeToElement(organization), TerraBasePrecondition.Absent, default);
            if (publicId != key)
                await state.PutAsync(AccountService.IdentitiesCollection, key, JsonSerializer.SerializeToElement(new AccountIdentityBinding(subject, publicId)), TerraBasePrecondition.Absent, default);
            await state.PutAsync(AccountService.AccountsCollection, key, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Absent, default);
            return Results.Ok(new { subject, storageKey = key, accountId = publicId });
        }

        private void CorruptAccount(string kind)
        {
            var key = AccountService.AccountStorageKey(subject);
            var accountKey = AccountService.AccountsCollection + "/" + key;
            var identityKey = AccountService.IdentitiesCollection + "/" + key;
            if (kind == "remove-identity") { state.Items.TryRemove(identityKey, out _); return; }
            var target = kind.StartsWith("identity-", StringComparison.Ordinal) ? identityKey : accountKey;
            var document = state.Items[target];
            if (kind.EndsWith("document-id", StringComparison.Ordinal))
                state.Items[target] = document with { Id = "foreign-document-identity" };
            else
            {
                var value = JsonSerializer.Deserialize<Dictionary<string, object>>(document.Document.GetRawText())!;
                if (kind.EndsWith("user", StringComparison.Ordinal)) value["UserId"] = new TerraScaleIdGenerator().NewId();
                else if (kind == "malformed-public-id") value["Id"] = "invalid-public-id";
                else value[target == identityKey ? "AccountId" : "Id"] = new TerraScaleIdGenerator().NewId();
                state.Items[target] = document with { Document = JsonSerializer.SerializeToElement(value) };
            }
        }

        private IResult Observations()
        {
            var plaintext = state.Items.Values.Select(item => item.Document.GetRawText()).ToArray();
            return Results.Json(new
            {
                refreshes,
                refreshPauses,
                billing = Billing.Observations(),
                codeExchanges,
                projectExchanges,
                projectScopeExchanges = projectScopeExchanges.ToArray(),
                projectReportReads,
                projectRequests = projectRequests.ToArray(),
                managementMutations = 0,
                managementRequests,
                projectManagementRequests,
                revocations,
                contractFailures,
                contractFailureMessages = contractFailureMessages.ToArray(),
                userinfoRequests,
                userinfoPauses,
                descendantIssuerGrants,
                publishedIssuer = CurrentIssuer,
                unsupportedIntrospectionRequests,
                forgedIdRoleTokens,
                storedClaimSpoof,
                identityAttempts,
                projectionWrites,
                subject,
                currentPlatformRole = platformRole,
                registeredPrincipals = principals.Values.Select(principal => new { userId = principal.Subject, email = principal.Email, platformRole = principal.PlatformRole }).ToArray(),
                issuedAdminRoleGrantsByUser = access.Values.GroupBy(grant => grant.Subject).ToDictionary(group => group.Key, group => group.Count(grant => grant.PlatformRole == "admin")),
                issuedAdminRoleGrants = access.Values.Count(grant => grant.PlatformRole == "admin"),
                encryptedCredentialRecords = state.Items.Keys.Count(key => key.StartsWith("auth_credentials/", StringComparison.Ordinal)),
                accessTokenHashRecords = state.Items.Keys.Count(key => key.StartsWith("dashboard_access_token_hashes/", StringComparison.Ordinal)),
                identities = state.Items.Where(entry => entry.Key.StartsWith(AccountService.IdentitiesCollection + "/", StringComparison.Ordinal))
                    .Select(entry => new { storageKey = entry.Value.Id, publicId = entry.Value.Document.GetProperty("AccountId").GetString() }).ToArray(),
                accounts = state.Items.Where(entry => entry.Key.StartsWith(AccountService.AccountsCollection + "/", StringComparison.Ordinal))
                    .Select(entry => new { storageKey = entry.Value.Id, publicId = entry.Value.Document.GetProperty("Id").GetString() }).ToArray(),
                credentialsEncrypted = access.Keys.Concat(refresh.Keys).All(token => plaintext.All(body => !body.Contains(token, StringComparison.Ordinal)))
            });
        }

        private bool Bearer(HttpContext http, out Grant grant)
        {
            var text = http.Request.Headers.Authorization.ToString();
            var token = text.StartsWith("Bearer ", StringComparison.Ordinal) ? text[7..] : "";
            return ActiveBearer(token, out grant);
        }

        private (string UserId, string Email)? ResolveActiveBearer(string token) => ActiveBearer(token, out var grant)
            ? (grant.Subject, grant.Email) : null;

        private bool ActiveBearer(string token, out Grant grant)
        {
            grant = access.GetValueOrDefault(token)!;
            if (grant is null || grant.Revoked || revoked || grant.ActivityExpiresAt <= clock.GetUtcNow()) return false;
            var principal = PrivatePrincipal(grant.Subject);
            if (principal is null || principal.Email != grant.Email || principal.AuthEpoch != grant.AuthEpoch) return false;
            try
            {
                var parts = token.Split('.');
                if (parts.Length != 3) return false;
                using var header = JsonDocument.Parse(UnB64(parts[0]));
                using var claims = JsonDocument.Parse(UnB64(parts[1]));
                if (header.RootElement.GetProperty("alg").GetString() != "ES256" || header.RootElement.GetProperty("typ").GetString() != "at+jwt" ||
                    header.RootElement.GetProperty("kid").GetString() != "test-signing-key" || claims.RootElement.GetProperty("iss").GetString() != CurrentIssuer ||
                    !Audience(claims.RootElement.GetProperty("aud")) || claims.RootElement.GetProperty("sub").GetString() != grant.Subject ||
                    claims.RootElement.GetProperty("email").GetString() != principal.Email || !claims.RootElement.GetProperty("email_verified").GetBoolean() ||
                    claims.RootElement.GetProperty("ts_auth_epoch").GetString() != principal.AuthEpoch.ToString(CultureInfo.InvariantCulture) ||
                    claims.RootElement.GetProperty("exp").GetInt64() <= clock.GetUtcNow().ToUnixTimeSeconds()) return false;
                lock (keyGate) return signingKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), UnB64(parts[2]),
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or CryptographicException)
            { return false; }
        }

        private static bool Basic(HttpContext http) => http.Request.Headers.Authorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":test-dashboard-client-secret-0123456789abcdef"));
        private bool Audience(JsonElement claim) => claim.ValueKind switch
        {
            JsonValueKind.String => claim.GetString() == CurrentIssuer,
            JsonValueKind.Array => claim.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == CurrentIssuer),
            _ => false
        };
        private static bool Confidential(HttpContext http, IFormCollection form) => Basic(http) || form["client_id"] == ClientId && form["client_secret"] == "test-dashboard-client-secret-0123456789abcdef";
        private IResult ContractFailure(string message)
        {
            Interlocked.Increment(ref contractFailures);
            contractFailureMessages.Enqueue(message);
            return Results.Json(new { error = "contract_failure", message }, statusCode: 400);
        }
        private static IResult OAuthError(string code, int status) => Results.Json(new { error = code }, statusCode: status);
        private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static byte[] UnB64(string text) => Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
        public void ReleaseRefresh() => refreshGate?.TrySetResult();
        public void ReleaseUserInfo() => userInfoGate?.TrySetResult();
        public void ReleaseAccountReservations() => accountReservations?.TrySetResult();
        public void Dispose() { userInfoGate?.TrySetResult(); accountReservations?.TrySetResult(); signingKey.Dispose(); wrongKey.Dispose(); }
        private sealed record Authorization(string Nonce, string Challenge, string RedirectUri, string Email);
        private sealed record DeviceAuthorization(string UserCode, DateTimeOffset ExpiresAt, string? Email = null, bool Denied = false);
        private sealed record Principal(string Subject, string Email, string Name, string Password, long AuthEpoch, string? PlatformRole);
        private sealed record Grant(string Subject, string Email, long AuthEpoch, string Family, DateTimeOffset ExpiresAt, string? PlatformRole)
        {
            public long AuthenticatedAt { get; set; }
            public bool Revoked { get; set; }
            public DateTimeOffset ActivityExpiresAt { get; set; } = ExpiresAt;
            public string AccessToken { get; set; } = "";
        }
        private sealed record ProjectGrant(Grant Session, string OrganizationId, string ProjectId, DateTimeOffset ExpiresAt, IReadOnlyList<string> Scopes);
    }
}
