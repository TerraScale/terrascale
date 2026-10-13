using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Headless;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Observability;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;
using TerraScale.Endpoints;
using ZLogger;

namespace TerraScale.Authentication;

/// <summary>Composes services, middleware and endpoints.</summary>
public static class App
{
    /// <summary>OpenAPI document title; clients key on it.</summary>
    public const string ApiTitle = "TerraScale Auth API";

    /// <summary>
    /// The region whose TerraBase takes every ts-auth write and every read but discovery's and the JWKS's, from every region.
    /// TerraBase checks a precondition only against the copy it is sent to, so single-use codes and tokens, unique claims
    /// and attempt counters hold only while one copy orders them; reading sessions, tokens, revocations and memberships
    /// there too makes a revocation or removal take effect everywhere at once. When this region's TerraBase does not answer,
    /// those operations fail closed (503) instead of moving elsewhere. Replication slice R12 (lattice-native ts-auth)
    /// removes the need.
    /// </summary>
    public const string AuthorityRegion = "gru";

    /// <summary>Service key of the authority's <see cref="TerraBaseDatabase"/> and <see cref="HttpClient"/>.</summary>
    public const string StorageAuthority = "terrabase-authority";

    /// <summary>
    /// TerraBase's internal listener on Fly's private network in this machine's region (<c>FLY_REGION</c>), for the reads of
    /// discovery and the JWKS (<see cref="AuthStore.GetLocalAsync"/>, <c>KeyRing.GetPublishedAsync</c>): app <c>ts-terrabase</c>, which replicates the platform
    /// databases between regions; in a <c>ts-pr-&lt;n&gt;</c> preview, whose one machine (in another Fly org) runs TerraBase
    /// next to ts-auth, that machine. When it does not answer, <see cref="TerraBaseOtherRegions"/> do. Off Fly (local
    /// development), the TerraBase on this machine, whose internal listener is on loopback there, in Development only: a
    /// production ts-auth off Fly has no storage to fall back on and refuses to start (<see cref="AuthConfigurationException"/>)
    /// instead of writing to whatever listens there. <paramref name="fly"/> holds Fly's machine environment
    /// (<c>FLY_APP_NAME</c>, <c>FLY_REGION</c>).
    /// </summary>
    public static Uri TerraBaseOrigin(AuthOptions options, IConfiguration fly) => TerraBaseApp(options, fly) switch
    {
        null => new("http://127.0.0.1:8081"),
        var app when fly["FLY_REGION"] is { Length: > 0 } region => new($"http://{region}.{app}.internal:8081"),
        _ => throw new AuthConfigurationException("FLY_REGION is unset: on Fly ts-auth stores in its own region's TerraBase"),
    };

    /// <summary>
    /// Where storage goes when this region's TerraBase does not answer: the nearest started machine of the app, then every
    /// started machine (the nearest can be this region's own, started but not answering). None off Fly.
    /// </summary>
    public static IReadOnlyList<DnsEndPoint> TerraBaseOtherRegions(AuthOptions options, IConfiguration fly) => TerraBaseApp(options, fly) switch
    {
        null => [],
        var app => [new($"top1.nearest.of.{app}.internal", 8081), new($"{app}.internal", 8081)],
    };

    /// <summary>
    /// The TerraBase that takes every write (<see cref="AuthorityRegion"/>'s), with no fallback; in a preview, its one machine.
    /// Null off Fly, where the local TerraBase is the only one.
    /// </summary>
    public static Uri? TerraBaseAuthority(AuthOptions options, IConfiguration fly) =>
        string.IsNullOrEmpty(fly["FLY_APP_NAME"]) ? null
        : TerraBaseApp(options, fly) == "ts-terrabase" ? new($"http://{AuthorityRegion}.ts-terrabase.internal:8081")
        : TerraBaseOrigin(options, fly);

    // The Fly app serving ts-auth's storage; null off Fly in Development (loopback).
    private static string? TerraBaseApp(AuthOptions options, IConfiguration fly) => fly["FLY_APP_NAME"] switch
    {
        null or "" when options.Development => null,
        null or "" => throw new AuthConfigurationException(
            "Off Fly (FLY_APP_NAME unset) ts-auth stores in the loopback TerraBase only in Development: set TS_AUTH_ENVIRONMENT=Development, or run it on Fly"),
        var app when app.StartsWith("ts-pr-", StringComparison.Ordinal) => app,
        _ => "ts-terrabase",
    };

    /// <summary>
    /// The TerraBase project holding the platform's own databases. TerraBase seeds it with this fixed id on every start
    /// (<c>PlatformDatabases</c> in src/TerraBase); the two must stay equal.
    /// </summary>
    public const string StorageProject = "00000000000000000000000002";

    /// <summary>ts-auth's database in <see cref="StorageProject"/>; ts-auth creates its collections there itself.</summary>
    public const string StorageDatabase = "ts-auth";

    /// <summary>
    /// Builds the application for validated options. <paramref name="configure"/> lets
    /// integration tests replace infrastructure before the container is built.
    /// </summary>
    public static WebApplication Build(AuthOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(options.Listen);
            kestrel.ListenForMetrics(options.MetricsListen);
        });

        builder.Services.AddSingleton(options);
        if (options.TrustedProxies.Count > 0)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(forwarding =>
            {
                forwarding.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
                forwarding.ForwardLimit = 1;
                forwarding.KnownProxies.Clear();
                forwarding.KnownIPNetworks.Clear();
                foreach (var proxy in options.TrustedProxies)
                {
                    forwarding.KnownProxies.Add(proxy);
                }
            });
        }

        builder.Services.AddSingleton(TimeProvider.System);
        // TerraBase machines' private listeners: no credential, they trust the private network. This region's, for discovery
        // and JWKS reads, falls back to other regions; the authority, for everything else, never does.
        builder.Services.AddSingleton(services => new HttpClient(TerraBaseTransport.CreateHandler(
            TerraBaseOtherRegions(options, services.GetRequiredService<IConfiguration>()),
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TerraBaseTransport)), services.GetService<TerraBaseTransport.HostResolver>())));
        builder.Services.AddSingleton(services => new TerraBaseDocumentsClient(services.GetRequiredService<HttpClient>(), new TerraBaseDocumentsClientOptions
        {
            BaseAddress = TerraBaseOrigin(options, services.GetRequiredService<IConfiguration>()),
        }).Database(StorageProject, StorageDatabase));
        builder.Services.AddKeyedSingleton(StorageAuthority, (services, _) => new HttpClient(TerraBaseTransport.CreateHandler(
            [], services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TerraBaseTransport)), services.GetService<TerraBaseTransport.HostResolver>())));
        builder.Services.AddKeyedSingleton(StorageAuthority, (services, _) =>
            TerraBaseAuthority(options, services.GetRequiredService<IConfiguration>()) is { } authority
                ? new TerraBaseDocumentsClient(services.GetRequiredKeyedService<HttpClient>(StorageAuthority), new TerraBaseDocumentsClientOptions
                {
                    BaseAddress = authority,
                }).Database(StorageProject, StorageDatabase)
                : services.GetRequiredService<TerraBaseDatabase>());
        builder.Services.AddSingleton(services => new AuthStore(services.GetRequiredService<TerraBaseDatabase>(),
            services.GetRequiredKeyedService<TerraBaseDatabase>(StorageAuthority), services.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<Users>();
        builder.Services.AddSingleton<Sessions>();
        builder.Services.AddSingleton<Passcodes>();
        builder.Services.AddSingleton<PasswordHasher>();
        builder.Services.AddSingleton<HeadlessAuth>();
        builder.Services.AddSingleton<TotpCredentials>();
        builder.Services.AddSingleton<PlatformDirectory>();
        builder.Services.AddSingleton<PlatformBootstrap>();
        builder.Services.AddSingleton<PlatformAuthentication>();
        builder.Services.AddSingleton<ApiKeys>();
        builder.Services.AddSingleton<PlatformGrants>();
        builder.Services.AddHttpClient(GitHubActionsGrant.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton<GitHubActionsKeys>();
        builder.Services.AddSingleton<Invitations>();
        builder.Services.AddHostedService<SessionPruner>();
        // Ready means the authority takes writes.
        builder.Services.AddSingleton(services => new TerraBaseStorageHealth(services.GetRequiredKeyedService<TerraBaseDatabase>(StorageAuthority)));
        builder.Services.AddSingleton<IStorageHealth, SigningKeyReadiness>();
        builder.Services.AddSingleton<ReadinessProbe>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ReadinessProbe>());
        builder.Services.AddSingleton<IDocumentStore, TerraBaseDocumentStore>();
        builder.Services.AddSingleton<KeyRing>();
        builder.Services.AddSingleton<SigningKeyRotation>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SigningKeyRotation>());
        builder.Services.AddSingleton<AttemptLimiter>();
        if (options.CloudflareEmail is { } cloudflareEmail)
        {
            builder.Services.AddSingleton(cloudflareEmail);
            builder.Services.AddHttpClient<IEmailSender, CloudflareEmailSender>(client => client.Timeout = TimeSpan.FromSeconds(30));
        }
        else
        {
            builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }

        builder.Services.AddSingleton<AuthEmails>();
        builder.Services.AddSingleton<EmailSettings>();
        builder.Services.AddSingleton<SignInMail>();
        builder.Services.AddHostedService(services => services.GetRequiredService<SignInMail>());
        builder.Services.AddOidc(options);

        builder.Services.AddTerraScaleEndpoints(typeof(App).Assembly, endpoints =>
        {
            endpoints.Title = ApiTitle;
            endpoints.Version = "v1";
        });
        builder.Services.AddAuthOpenApi();
        builder.AddAuthTelemetry(options);

        configure?.Invoke(builder);

        // This pipeline owns forwarding order and trust, even when ASP.NET's automatic forwarding setting is enabled.
        builder.Configuration["ForwardedHeaders_Enabled"] = "false";

        var app = builder.Build();
        // Resolved now, so a ts-auth without storage refuses to start (see TerraBaseOrigin); tests may register their own.
        app.Services.GetRequiredService<TerraBaseDatabase>();
        // Hashes its timing equalizer now, so the first password check of an unknown account costs no more than any other.
        app.Services.GetRequiredService<PasswordHasher>();
        if (options.CloudflareEmail is null && !options.Development)
        {
            app.Logger.ZLogWarning($"CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_EMAIL_TOKEN are not configured: email is logged, not delivered");
        }

        app.UseAuthMetrics();
        app.UseMiddleware<ErrorEnvelopeMiddleware>();
        TokenRateLimitMiddleware.UsePeerGuard(app);
        if (options.TrustedProxies.Count > 0)
        {
            app.Use((HttpContext context, RequestDelegate next) =>
            {
                // Joining header values and native splitting drop empty entries. Inspect the last raw wire value.
                var values = context.Request.Headers["X-Forwarded-For"];
                var last = values.Count == 0 ? string.Empty : values[^1] ?? string.Empty;
                if (last.AsSpan(last.LastIndexOf(',') + 1).Trim().Trim('"').Trim().IsEmpty)
                {
                    context.Request.Headers.Remove("X-Forwarded-For");
                }

                return next(context);
            });
            app.UseForwardedHeaders();
            TokenRateLimitMiddleware.UseClientGuard(app);
        }
        // OpenIddict answers discovery, JWKS and protocol errors here and validates passthrough requests first.
        app.UseAuthentication();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        return app;
    }
}
