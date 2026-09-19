namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Api;
using TerraBase.Integration;
using TerraBase.Security;
using TerraScale.DotNet;

/// <summary>
/// Boots the real ts-auth service against a real embedded TerraBase engine,
/// reached through the real TerraScale.DotNet SDK — the full end-user stack
/// with no sockets and no fakes.
/// </summary>
public sealed class AuthServiceFixture : IAsyncDisposable
{
    private const string OperatorKey = "operator-test-key";
    private readonly EmbeddedTerraBaseHost engine;
    private readonly WebApplication application;
    private readonly string root;

    public HttpClient Http { get; }
    public TerraScaleClient Sdk { get; }
    public string Database { get; }

    private AuthServiceFixture(EmbeddedTerraBaseHost engine, WebApplication application, HttpClient http, TerraScaleClient sdk, string database, string root)
    {
        this.engine = engine;
        this.application = application;
        Http = http;
        Sdk = sdk;
        Database = database;
        this.root = root;
    }

    public static async Task<AuthServiceFixture> StartAsync()
    {
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

        var engineClient = new TerraScaleClient(
            new HttpClient(engine.CreateHandler()) { BaseAddress = new Uri("http://127.0.0.1/") },
            new TerraScaleClientOptions { BaseAddress = new Uri("http://127.0.0.1/"), ApiKey = "e2e-key" });

        Environment.SetEnvironmentVariable("TS_AUTH_PUBLIC_ORIGIN", "https://auth.test");
        Environment.SetEnvironmentVariable("TS_AUTH_TERRASCALE_API_KEY", "e2e-key");
        Environment.SetEnvironmentVariable("TS_AUTH_DATABASE", database);
        Environment.SetEnvironmentVariable("TS_AUTH_MASTER_KEY", Convert.ToBase64String(new byte[32]));
        Environment.SetEnvironmentVariable("TS_AUTH_OPERATOR_KEY", OperatorKey);

        var (builder, options) = TerraScale.Auth.App.CreateBuilder([]);
        builder.WebHost.UseTestServer();
        builder.Services.RemoveAll<TerraScaleClient>();
        builder.Services.AddSingleton(engineClient);
        var application = builder.Build();
        TerraScale.Auth.App.Configure(application, options);
        await application.StartAsync();

        var http = application.GetTestClient();

        // Wait for schema bootstrap to finish before the first test runs.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var probe = await http.GetAsync("/healthz");
            if (probe.IsSuccessStatusCode)
                break;
            await Task.Delay(100);
        }

        return new AuthServiceFixture(engine, application, http, engineClient, database, root);
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

    /// <summary>Direct store access for arranging/inspecting state in tests.</summary>
    public TerraScale.Auth.Store.AuthStore Store => new(Sdk, Database);

    /// <summary>The master key the fixture configured (base64 of 32 zero bytes).</summary>
    public static byte[] MasterKey => new byte[32];

    public async ValueTask DisposeAsync()
    {
        await application.StopAsync();
        await application.DisposeAsync();
        await engine.DisposeAsync();
        if (Directory.Exists(root))
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
