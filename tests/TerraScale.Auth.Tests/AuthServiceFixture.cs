namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Api;
using TerraBase.Integration;
using TerraBase.Security;
using TerraScale.DotNet;

/// <summary>
/// Boots the real ts-auth service against a real embedded TerraBase engine,
/// reached through the real TerraScale.DotNet SDK — the full end-user stack
/// with no fakes. The service listens on a real loopback socket so HttpClient
/// and the Playwright browser drive the exact same server.
/// </summary>
public sealed class AuthServiceFixture : IAsyncDisposable
{
    private const string OperatorKey = "operator-test-key";
    private readonly EmbeddedTerraBaseHost? engine;
    private readonly WebApplication application;
    private readonly BreakableHandler engineHandler;
    private readonly string? root;
    private readonly IReadOnlyDictionary<string, string?> savedEnvironment;

    public HttpClient Http { get; }
    public TerraScaleClient Sdk { get; }
    public string Database { get; }

    /// <summary>The real loopback origin the service listens on (also TS_AUTH_PUBLIC_ORIGIN).</summary>
    public Uri Origin { get; }

    private AuthServiceFixture(EmbeddedTerraBaseHost? engine, WebApplication application, HttpClient http, TerraScaleClient sdk, BreakableHandler engineHandler, string database, string? root, IReadOnlyDictionary<string, string?> savedEnvironment, Uri origin)
    {
        this.engine = engine;
        this.application = application;
        Http = http;
        Sdk = sdk;
        this.engineHandler = engineHandler;
        Database = database;
        this.root = root;
        this.savedEnvironment = savedEnvironment;
        Origin = origin;
    }

    /// <summary>
    /// Reserves a free loopback port for a service boot: the public origin is baked
    /// into the app at build time, so the port has to be known before Kestrel binds.
    /// </summary>
    internal static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Boots the app half of the fixture on a real loopback listener and
    /// returns it with an <see cref="HttpClient"/> (no auto-redirect: tests assert
    /// 302s and Location headers themselves) pointed at it.</summary>
    internal static async Task<(WebApplication Application, HttpClient Http, Uri Origin)> StartAppAsync(
        IReadOnlyDictionary<string, string?> environment,
        TerraScaleClient engineClient,
        Action<IWebHostBuilder> configureHost)
    {
        // localhost (not 127.0.0.1): Chromium accepts Secure cookies over plain http
        // only for the localhost name, and the session cookie is always Secure.
        var origin = new Uri($"http://localhost:{ReserveLoopbackPort()}/");
        // The public origin must be the real listener: absolute redirects and email
        // links have to be reachable by the browser driving the hosted UI.
        var hostEnvironment = new Dictionary<string, string?>(environment)
        {
            ["TS_AUTH_PUBLIC_ORIGIN"] = origin.ToString(),
        };

        var saved = hostEnvironment.Keys.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
        WebApplication application;
        try
        {
            foreach (var (name, value) in hostEnvironment)
                Environment.SetEnvironmentVariable(name, value);

            var (builder, options) = TerraScale.Auth.App.CreateBuilder([]);
            configureHost(builder.WebHost);
            builder.WebHost.UseUrls(origin.ToString());
            builder.Services.RemoveAll<TerraScaleClient>();
            builder.Services.AddSingleton(engineClient);
            application = builder.Build();
            TerraScale.Auth.App.Configure(application, options);
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
        await application.StartAsync();
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = origin };

        // Wait for schema bootstrap to finish before the first test runs.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var probe = await http.GetAsync("/healthz");
            if (probe.IsSuccessStatusCode)
                break;
            await Task.Delay(100);
        }
        return (application, http, origin);
    }

    public static async Task<AuthServiceFixture> StartAsync()
    {
        // TS_AUTH_E2E_ENGINE_URL points the whole suite at a REAL external TerraBase
        // member (real Kestrel, real sockets) instead of the in-process engine — the
        // production path end to end. Set TS_AUTH_E2E_API_KEY / TS_AUTH_E2E_DATABASE
        // to match how the member was launched (see tools/TerraBaseDevMember).
        // The member's database must be FRESH per run: some tests claim fixed
        // identifiers, which collide with leftovers from a previous run's data.
        var externalUrl = Environment.GetEnvironmentVariable("TS_AUTH_E2E_ENGINE_URL");
        if (externalUrl is not null)
            return await StartExternalAsync(externalUrl);

        var database = "tsauth-e2e-" + Guid.NewGuid().ToString("N")[..8];
        var root = Path.Combine(Path.GetTempPath(), "ts-auth-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var engine = TerraBaseHost.CreateEmbedded(builder =>
        {
            builder.Services.AddSingleton<IStartupFilter, AuthenticatedPrincipalStartupFilter>();
            builder.Services.AddSingleton<ITenantAuthorizationHook, AllowAllAuthorizationHook>();
            builder.Configuration["TERRABASE_REGION"] = "GRU";
            builder.Configuration["TERRABASE_DATABASE_PATH"] = root;
            builder.Configuration["TERRABASE_ALLOW_SCHEMALESS"] = "true";
            builder.Configuration["TERRABASE_DATABASE_ROUTE"] = database;
            builder.Configuration["TERRABASE_DATABASE_TENANT"] = "tsauth-e2e";
            builder.Configuration["TERRABASE_DATABASE_ULID"] = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
            builder.Configuration["TERRABASE_DATABASE_MODE"] = "explicit";
            builder.Configuration["TERRABASE_DATABASE_HOME_REGION"] = "GRU";
            builder.Configuration["TERRABASE_DATABASE_SELECTED_REGIONS"] = "GRU";
        });
        await engine.Application.StartAsync();

        var engineHandler = new BreakableHandler(engine.CreateHandler());
        var engineClient = new TerraScaleClient(
            new HttpClient(engineHandler) { BaseAddress = new Uri("http://127.0.0.1/") },
            new TerraScaleClientOptions { BaseAddress = new Uri("http://127.0.0.1/"), ApiKey = "e2e-key" });

        var environment = new Dictionary<string, string?>
        {
            ["TS_AUTH_TERRASCALE_API_KEY"] = "e2e-key",
            ["TS_AUTH_DATABASE"] = database,
            ["TS_AUTH_MASTER_KEY"] = Convert.ToBase64String(new byte[32]),
            ["TS_AUTH_AUDIT_SIGNING_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            ["TS_AUTH_AUDIT_VERIFY_KEYS"] = Convert.ToBase64String(Enumerable.Range(33, 32).Select(i => (byte)i).ToArray()),
            ["TS_AUTH_OPERATOR_KEY"] = OperatorKey,
            ["TS_AUTH_METRICS_KEY"] = "metrics-test-key",
        };
        // Set for the build, restored on dispose: private fixture boots must not leave
        // their database/key pointing out of a disposed engine for later boots to inherit.
        var savedEnvironment = environment.Keys.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
        foreach (var (name, value) in environment)
            Environment.SetEnvironmentVariable(name, value);

        var (application, http, origin) = await StartAppAsync(environment, engineClient, _ => { });

        return new AuthServiceFixture(engine, application, http, engineClient, engineHandler, database, root, savedEnvironment, origin);
    }

    /// <summary>Boots the service against a real external TerraBase member over
    /// real sockets. The service's client is swapped for one through the same
    /// breakable handler so outage probes still work; the member itself is not
    /// owned by the fixture and outlives it.</summary>
    private static async Task<AuthServiceFixture> StartExternalAsync(string externalUrl)
    {
        var database = Environment.GetEnvironmentVariable("TS_AUTH_E2E_DATABASE") ?? "ts-auth-dev";
        var apiKey = Environment.GetEnvironmentVariable("TS_AUTH_E2E_API_KEY") ?? "dev";

        var engineHandler = new BreakableHandler(new SocketsHttpHandler { AllowAutoRedirect = false });
        var engineClient = new TerraScaleClient(
            new HttpClient(engineHandler) { BaseAddress = new Uri(externalUrl) },
            new TerraScaleClientOptions { BaseAddress = new Uri(externalUrl), ApiKey = apiKey });

        var environment = new Dictionary<string, string?>
        {
            ["TS_AUTH_TERRASCALE_API_KEY"] = apiKey,
            ["TS_AUTH_DATABASE"] = database,
            ["TS_AUTH_MASTER_KEY"] = Convert.ToBase64String(new byte[32]),
            ["TS_AUTH_AUDIT_SIGNING_KEY"] = Convert.ToBase64String(AuditSigningKey),
            ["TS_AUTH_AUDIT_VERIFY_KEYS"] = Convert.ToBase64String(AuditPreviousSigningKey),
            ["TS_AUTH_OPERATOR_KEY"] = OperatorKey,
            ["TS_AUTH_METRICS_KEY"] = "metrics-test-key",
        };

        var (application, http, origin) = await StartAppAsync(environment, engineClient, _ => { });

        return new AuthServiceFixture(null!, application, http, engineClient, engineHandler, database, null!, new Dictionary<string, string?>(), origin);
    }


    /// <summary>Shared lazy singleton across all E2E test classes in this assembly.</summary>
    private static readonly Task<AuthServiceFixture> Shared = StartSharedAsync();

    private static async Task<AuthServiceFixture> StartSharedAsync()
    {
        var fixture = await StartAsync();
        return fixture;
    }

    /// <summary>Returns the shared running service.</summary>
    public static Task<AuthServiceFixture> GetAsync() => Shared;

    /// <summary>Creates a fresh instance + default sk_ API key via the operator endpoint.</summary>
    public async Task<(string InstanceId, string InstanceSlug, string ApiKey)> CreateInstanceAsync(string slug)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/instances")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { slug, name = slug }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "operator-test-key");
        var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return (
            body.GetProperty("instance").GetProperty("id").GetString()!,
            body.GetProperty("instance").GetProperty("slug").GetString()!,
            body.GetProperty("api_key").GetString()!);
    }

    /// <summary>
    /// Severs the app's connection to the engine (the engine itself keeps running):
    /// every store read/write fails from here on — failure-path probes. The real
    /// engine stays real; only the wire is cut, like a network outage.
    /// </summary>
    public void BreakEngineConnection() => engineHandler.Break();

    /// <summary>Direct store access for arranging/inspecting state in tests.</summary>
    public TerraScale.Auth.Store.AuthStore Store => new(Sdk, Database, AuditSigningKey, [AuditPreviousSigningKey]);

    /// <summary>The master key the fixture configured (base64 of 32 zero bytes).</summary>
    public static byte[] MasterKey => new byte[32];

    public static byte[] AuditSigningKey => Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    public static byte[] AuditPreviousSigningKey => Enumerable.Range(33, 32).Select(i => (byte)i).ToArray();

    public async ValueTask DisposeAsync()
    {
        await application.StopAsync();
        await application.DisposeAsync();
        // External mode owns no engine and no temp directory.
        if (engine is not null)
            await engine.DisposeAsync();
        foreach (var (name, value) in savedEnvironment)
            Environment.SetEnvironmentVariable(name, value);
        if (root is not null && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class AuthenticatedPrincipalStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, continuation) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "ts-auth-e2e")],
                    "ts-auth-e2e"));
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
}

/// <summary>
/// Wraps the real embedded-engine handler; <see cref="Break"/> makes every call fail
/// like a network outage while the engine behind it stays untouched.
/// </summary>
internal sealed class BreakableHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private volatile bool broken;

    public void Break() => broken = true;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        broken
            ? throw new HttpRequestException("engine unreachable (test-induced outage)")
            : base.SendAsync(request, cancellationToken);
}
