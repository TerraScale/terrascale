#if TS_AUTH_STACK
namespace TerraScale.Dashboard2.SlowTests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase;
using TerraBase.Api;
using TerraBase.Security;
using TerraScale.DotNet;

/// <summary>
/// Boots the live dashboard stack entirely in-process, mirroring the ts-auth
/// repo's own E2E fixture: a real embedded TerraBase engine backs ts-auth
/// (whose client is swapped for the engine's in-memory handler), the real
/// ts-auth service listens on a reserved loopback port, and a real TerraBase
/// member — Kestrel over a real socket — serves the dashboard's Terrabase
/// state database plus a minimal dev control plane for database provisioning
/// (the platform's /api/v1/databases surface no member serves). The dashboard
/// itself runs through WebApplicationFactory in live mode and talks to both
/// halves over real HTTP, exactly as deployed.
/// </summary>
public sealed class LiveDashboardFixture : IAsyncDisposable
{
    public const string OperatorKey = "operator-test-key";
    public const string EngineApiKey = "e2e-key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly EmbeddedTerraBaseHost authEngine;
    private readonly WebApplication authApp;
    private readonly WebApplication stateMember;
    private readonly DevControlPlane controlPlane;
    private readonly string root;

    private LiveDashboardFixture(
        EmbeddedTerraBaseHost authEngine,
        WebApplication authApp,
        HttpClient authHttp,
        TerraScaleClient stateSdk,
        WebApplication stateMember,
        DevControlPlane controlPlane,
        string stateDatabase,
        string root,
        Uri stateUrl,
        Uri authOrigin,
        string slug,
        string managementKey,
        string clientId,
        string clientSecret)
    {
        this.authEngine = authEngine;
        this.authApp = authApp;
        this.stateMember = stateMember;
        this.controlPlane = controlPlane;
        this.root = root;
        AuthHttp = authHttp;
        StateSdk = stateSdk;
        StateDatabase = stateDatabase;
        StateUrl = stateUrl;
        AuthOrigin = authOrigin;
        Slug = slug;
        ManagementKey = managementKey;
        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    /// <summary>Client pointed at the real ts-auth service (no redirects, no cookie jar).</summary>
    public HttpClient AuthHttp { get; }

    /// <summary>SDK over the real state member socket — for asserting on stored state.</summary>
    public TerraScaleClient StateSdk { get; }

    /// <summary>The Terrabase database holding the dashboard's durable account worlds.</summary>
    public string StateDatabase { get; }

    /// <summary>The real loopback origin of the TerraBase state member.</summary>
    public Uri StateUrl { get; }

    /// <summary>The real loopback origin ts-auth listens on.</summary>
    public Uri AuthOrigin { get; }

    /// <summary>The ts-auth instance slug the dashboard signs in against.</summary>
    public string Slug { get; }

    /// <summary>The instance's sk_ management key (user + client administration).</summary>
    public string ManagementKey { get; }

    public string ClientId { get; }

    public string ClientSecret { get; }

    private static Task<LiveDashboardFixture>? shared;

    /// <summary>One stack for every live test in the run: booting two full
    /// stacks concurrently trips ts-auth's instance-creation rate limit.</summary>
    public static Task<LiveDashboardFixture> Shared => shared ??= StartAsync();

    /// <summary>Boots the whole live stack: engines, ts-auth, instance, OAuth client.</summary>
    public static async Task<LiveDashboardFixture> StartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "tsdash-live-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // Engine A: ts-auth's database, embedded like the ts-auth fixture.
        var authDatabase = "tsauth-live-" + Guid.NewGuid().ToString("N")[..8];
        var authEngine = TerraBaseHost.CreateEmbedded(builder => ConfigureEngine(builder, authDatabase, Path.Combine(root, "auth-engine")));
        await authEngine.Application.StartAsync();
        var authEngineClient = new TerraScaleClient(
            new HttpClient(authEngine.CreateHandler()) { BaseAddress = new Uri("http://127.0.0.1/") },
            new TerraScaleClientOptions { BaseAddress = new Uri("http://127.0.0.1/"), ApiKey = EngineApiKey });

        // ts-auth on a real loopback socket, wired to the embedded engine.
        var authOrigin = new Uri($"http://localhost:{ReserveLoopbackPort()}/");
        var environment = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["TS_AUTH_PUBLIC_ORIGIN"] = authOrigin.ToString(),
            ["TS_AUTH_TERRASCALE_API_URL"] = "http://127.0.0.1/",
            ["TS_AUTH_TERRASCALE_API_KEY"] = EngineApiKey,
            ["TS_AUTH_DATABASE"] = authDatabase,
            ["TS_AUTH_MASTER_KEY"] = Convert.ToBase64String(new byte[32]),
            ["TS_AUTH_AUDIT_SIGNING_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            ["TS_AUTH_AUDIT_VERIFY_KEYS"] = Convert.ToBase64String(Enumerable.Range(33, 32).Select(i => (byte)i).ToArray()),
            ["TS_AUTH_OPERATOR_KEY"] = OperatorKey,
        };
        var saved = environment.Keys.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
        WebApplication authApp;
        try
        {
            foreach (var (name, value) in environment)
                Environment.SetEnvironmentVariable(name, value);

            var (builder, options) = TerraScale.Auth.App.CreateBuilder([]);
            builder.WebHost.UseUrls(authOrigin.ToString());
            builder.Services.RemoveAll<TerraScaleClient>();
            builder.Services.AddSingleton(authEngineClient);
            authApp = builder.Build();
            TerraScale.Auth.App.Configure(authApp, options);
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }

        await authApp.StartAsync();
        var authHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = authOrigin };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var probe = await authHttp.GetAsync("/healthz");
                if (probe.IsSuccessStatusCode)
                    break;
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(100);
        }

        // Engine B: a real Kestrel TerraBase member serving the dashboard's state
        // database plus the dev control plane the dashboard's data plane dials.
        var stateDatabase = "tsdash-live-" + Guid.NewGuid().ToString("N")[..8];
        var stateUrl = new Uri($"http://127.0.0.1:{ReserveLoopbackPort()}");
        var controlPlane = new DevControlPlane(root, stateDatabase, EngineApiKey);
        var stateMember = TerraBaseHost.Create(stateUrl.ToString(), builder =>
        {
            builder.Services.AddSingleton<IStartupFilter>(new ControlPlaneStartupFilter(controlPlane));
            ConfigureEngine(builder, stateDatabase, Path.Combine(root, "state-engine"));
        });
        await stateMember.StartAsync();
        var stateSdk = new TerraScaleClient(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = stateUrl },
            new TerraScaleClientOptions { BaseAddress = stateUrl, ApiKey = EngineApiKey });

        // The ts-auth instance + OAuth client the dashboard lives behind.
        var (slug, managementKey) = await CreateInstanceAsync(authHttp);
        var (clientId, clientSecret) = await CreateOAuthClientAsync(authHttp, slug, managementKey);

        return new LiveDashboardFixture(
            authEngine, authApp, authHttp, stateSdk, stateMember, controlPlane, stateDatabase, root,
            stateUrl, authOrigin, slug, managementKey, clientId, clientSecret);
    }

    /// <summary>A dashboard app instance in live mode against the running stack.
    /// Create a second one to prove Terrabase state survives a dashboard restart.</summary>
    public WebApplicationFactory<DashboardStore> CreateDashboardFactory() =>
        new WebApplicationFactory<DashboardStore>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("TS_DASHBOARD_MODE", "live");
            builder.UseSetting("TS2_E2E", "1");
            builder.UseSetting("TS_DASHBOARD_IDP_ORIGIN", AuthOrigin.ToString());
            builder.UseSetting("TS_DASHBOARD_PUBLIC_ORIGIN", "http://localhost");
            builder.UseSetting("TS_DASHBOARD_IDP_INSTANCE", Slug);
            builder.UseSetting("TS_DASHBOARD_IDP_CLIENT_ID", ClientId);
            builder.UseSetting("TS_DASHBOARD_IDP_CLIENT_SECRET", ClientSecret);
            builder.UseSetting("TS_DASHBOARD_IDP_MANAGEMENT_KEY", ManagementKey);
            builder.UseSetting("TS_DASHBOARD_STATE_URL", StateUrl.ToString());
            builder.UseSetting("TS_DASHBOARD_STATE_API_KEY", EngineApiKey);
            builder.UseSetting("TS_DASHBOARD_STATE_DATABASE", StateDatabase);
        });

    /// <summary>A cookie-handling dashboard client (no auto-redirect: tests assert 302s).</summary>
    public HttpClient CreateDashboardClient(WebApplicationFactory<DashboardStore> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost/"),
        });

    private static async Task<(string Slug, string ManagementKey)> CreateInstanceAsync(HttpClient authHttp)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/instances")
        {
            Content = JsonContent.Create(new { slug = "dash-live-" + Guid.NewGuid().ToString("N")[..8], name = "Dashboard Live" }),
        };
        request.Headers.Authorization = new("Bearer", OperatorKey);
        using var response = await authHttp.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("instance").GetProperty("slug").GetString()!, body.GetProperty("api_key").GetString()!);
    }

    private static async Task<(string ClientId, string ClientSecret)> CreateOAuthClientAsync(HttpClient authHttp, string slug, string managementKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/{slug}/oauth-clients")
        {
            Content = JsonContent.Create(new
            {
                name = "TerraScale Dashboard",
                redirect_uris = new[] { "http://localhost/api/v1/auth/oidc/callback" },
                @public = false,
            }),
        };
        request.Headers.Authorization = new("Bearer", managementKey);
        using var response = await authHttp.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("client").GetProperty("clientId").GetString()!, body.GetProperty("client_secret").GetString()!);
    }

    /// <summary>The dashboard's world document id for one identity-provider subject.</summary>
    public static string WorldDocumentId(string subject) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(subject))).ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        AuthHttp.Dispose();
        await stateMember.DisposeAsync();
        await controlPlane.DisposeAsync();
        await authApp.DisposeAsync();
        await authEngine.DisposeAsync();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static void ConfigureEngine(WebApplicationBuilder builder, string database, string path)
    {
        Directory.CreateDirectory(path);
        builder.Services.AddSingleton<IStartupFilter>(new AuthenticatedPrincipalStartupFilter());
        builder.Services.AddSingleton<ITenantAuthorizationHook, AllowAllAuthorizationHook>();
        builder.Configuration["TERRABASE_REGION"] = "GRU";
        builder.Configuration["TERRABASE_DATABASE_PATH"] = path;
        builder.Configuration["TERRABASE_ALLOW_SCHEMALESS"] = "true";
        builder.Configuration["TERRABASE_DATABASE_ROUTE"] = database;
        builder.Configuration["TERRABASE_DATABASE_TENANT"] = "tsdash-live-e2e";
        builder.Configuration["TERRABASE_DATABASE_ULID"] = EntityId.New().ToString();
        builder.Configuration["TERRABASE_DATABASE_MODE"] = "explicit";
        builder.Configuration["TERRABASE_DATABASE_HOME_REGION"] = "GRU";
        builder.Configuration["TERRABASE_DATABASE_SELECTED_REGIONS"] = "GRU";
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Installs the authenticated principal the platform gateway provides in production.</summary>
    private sealed class AuthenticatedPrincipalStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, continuation) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "tsdash-live-e2e")],
                    "tsdash-live-e2e"));
                await continuation().ConfigureAwait(false);
            });
            next(application);
        };
    }

    private sealed class AllowAllAuthorizationHook : ITenantAuthorizationHook
    {
        public ValueTask<TenantAuthorizationDecision> AuthorizeAsync(
            TenantAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TenantAuthorizationDecision.AllowUntil(DateTimeOffset.UtcNow.AddSeconds(10)));
    }

    /// <summary>The state member's front door: authenticates the caller (the
    /// admin key reaches everything; an issued credential only its scoped
    /// databases), then routes the platform's database-management surface and
    /// the document routes of plane-provisioned databases onto their real
    /// embedded engines. Everything else falls through to the member itself.</summary>
    private sealed class ControlPlaneStartupFilter(DevControlPlane plane) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, continuation) =>
            {
                if (!HttpMethods.IsGet(context.Request.Path) || !context.Request.Path.StartsWithSegments("/health"))
                {
                    if (!plane.IsAuthorized(context))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsync("unauthorized");
                        return;
                    }
                }

                if (await plane.TryHandleAsync(context))
                    return;
                await continuation().ConfigureAwait(false);
            });
            next(application);
        };
    }

    private sealed class DevControlPlane : IAsyncDisposable
    {
        private readonly string root;
        private readonly string adminKey;
        private readonly ConcurrentDictionary<string, WireDatabase> databases = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, EmbeddedTerraBaseHost> enginesByDatabase = new(StringComparer.Ordinal);
        private readonly List<EmbeddedTerraBaseHost> engines = [];
        private readonly ConcurrentDictionary<string, IssuedCredential> credentialsBySecret = new(StringComparer.Ordinal);

        /// <summary>A credential the plane issued; an empty database scope means
        /// the key reaches every plane database, otherwise exactly the listed ones.</summary>
        public sealed record IssuedCredential(string Id, string Name, string[] Databases);

        public DevControlPlane(string root, string stateDatabase, string adminKey)
        {
            this.root = root;
            this.adminKey = adminKey;
            // The member's own state binding is a real database too.
            databases[stateDatabase] = Record(stateDatabase);
        }

        /// <summary>Authenticate the request: the admin key reaches everything,
        /// an issued credential only its scoped databases.</summary>
        public bool IsAuthorized(HttpContext context)
        {
            var header = context.Request.Headers.Authorization.ToString();
            var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : string.Empty;
            if (presented.Length == 0)
                return false;
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(adminKey)))
                return true;
            if (!credentialsBySecret.TryGetValue(presented, out var credential))
                return false;
            if (credential.Databases.Length == 0)
                return true;
            return credential.Databases.Contains(RouteDatabase(context.Request.Path));
        }

        public async Task<bool> TryHandleAsync(HttpContext context)
        {
            if (context.Request.Path.StartsWithSegments("/api/v1/credentials", out var credentialRemainder))
            {
                if ((credentialRemainder.Value?.Length ?? 0) > 0)
                    return false;
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    await IssueCredentialAsync(context);
                    return true;
                }

                return false;
            }

            if (!context.Request.Path.StartsWithSegments("/api/v1/databases", out var remaining))
                return false;

            var segments = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (segments.Length == 0)
            {
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    await CreateAsync(context);
                    return true;
                }

                if (HttpMethods.IsGet(context.Request.Method))
                {
                    await WriteJsonAsync(context, 200, new { databases = databases.Values.OrderBy(d => d.Name).ToArray() });
                    return true;
                }

                return false;
            }

            var name = Uri.UnescapeDataString(segments[0]);

            if (segments.Length == 1 && HttpMethods.IsGet(context.Request.Method))
            {
                if (databases.TryGetValue(name, out var record))
                    await WriteJsonAsync(context, 200, record);
                else
                    await WriteJsonAsync(context, 404, new { error = "not_found", message = $"no database named {name}" });
                return true;
            }

            // Document and collection routes for a plane-provisioned database
            // proxy into that database's real embedded engine.
            if (segments.Length >= 2 && enginesByDatabase.ContainsKey(name))
            {
                await ProxyAsync(context, name);
                return true;
            }

            return false;
        }

        /// <summary>Database name carried by a /api/v1/databases/{name}/... route.</summary>
        private static string RouteDatabase(PathString path)
        {
            if (path.StartsWithSegments("/api/v1/databases", out var remaining))
            {
                var first = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (first is { Length: > 0 })
                    return Uri.UnescapeDataString(first);
            }

            return string.Empty;
        }

        private async Task IssueCredentialAsync(HttpContext context)
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var root = body.RootElement;
            var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                await WriteJsonAsync(context, 400, new { error = "invalid_request", message = "name is required" });
                return;
            }

            var scopes = root.TryGetProperty("scopes", out var scopesValue)
                ? scopesValue.EnumerateArray().Select(s => s.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                : [];
            var database = root.TryGetProperty("database", out var databaseValue) ? databaseValue.GetString() : null;
            var secret = "tsk_live_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var id = "key_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
            credentialsBySecret[secret] = new IssuedCredential(id, name, string.IsNullOrWhiteSpace(database) ? [] : [database]);
            await WriteJsonAsync(context, 201, new { id, name, secret, scopes });
        }

        /// <summary>Forwards one request to the plane database's engine — the
        /// real TerraBase document pipeline, in-memory hop included.</summary>
        private async Task ProxyAsync(HttpContext context, string database)
        {
            if (!enginesByDatabase.TryGetValue(database, out var engine))
            {
                await WriteJsonAsync(context, 404, new { error = "not_found", message = $"no database named {database}" });
                return;
            }

            var client = new HttpClient(engine.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") };
            using var upstream = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
            if (context.Request.ContentLength is > 0 or null && (context.Request.ContentLength ?? -1) != 0 && context.Request.Body is { CanRead: true })
            {
                using var memory = new MemoryStream();
                await context.Request.Body.CopyToAsync(memory, context.RequestAborted);
                upstream.Content = new ByteArrayContent(memory.ToArray());
                if (context.Request.ContentType is { Length: > 0 } contentType)
                    upstream.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            foreach (var header in context.Request.Headers)
            {
                if (!header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !header.Key.StartsWith("Host", StringComparison.OrdinalIgnoreCase) && !header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    upstream.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            using var response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();
            foreach (var header in response.Content.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();
            context.Response.Headers.Remove("Transfer-Encoding");
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }

        private async Task CreateAsync(HttpContext context)
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var name = body.RootElement.TryGetProperty("name", out var value) ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                await WriteJsonAsync(context, 400, new { error = "invalid_request", message = "name is required" });
                return;
            }

            if (databases.TryGetValue(name, out var existing))
            {
                await WriteJsonAsync(context, 200, existing);
                return;
            }

            var engine = TerraBaseHost.CreateEmbedded(builder => ConfigureEngine(builder, name, Path.Combine(root, "db-" + name)));
            await engine.Application.StartAsync();
            enginesByDatabase[name] = engine;
            lock (engines)
            {
                engines.Add(engine);
            }

            var record = Record(name);
            databases[name] = record;
            await WriteJsonAsync(context, 201, record);
        }

        private static WireDatabase Record(string name) =>
            new(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), false, name, null, "GRU", "br-only");

        public async ValueTask DisposeAsync()
        {
            EmbeddedTerraBaseHost[] captured;
            lock (engines)
            {
                captured = [.. engines];
                engines.Clear();
            }

            foreach (var engine in captured)
                await engine.DisposeAsync();
        }

        private static async Task WriteJsonAsync(HttpContext context, int statusCode, object body)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
        }

        private sealed record WireDatabase(
            [property: JsonPropertyName("created_at")] long CreatedAt,
            [property: JsonPropertyName("deleted")] bool Deleted,
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("parent")] string? Parent,
            [property: JsonPropertyName("placement")] string Placement,
            [property: JsonPropertyName("residency")] string Residency);
    }
}
#endif
