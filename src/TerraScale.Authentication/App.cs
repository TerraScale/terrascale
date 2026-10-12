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
    /// TerraBase's internal listener on Fly's private network, nearest machine first: app <c>ts-terrabase</c>, except in
    /// a <c>ts-pr-&lt;n&gt;</c> preview, whose one machine (in another Fly org) runs TerraBase next to ts-auth.
    /// </summary>
    public static readonly Uri TerraBaseOrigin = new($"http://top1.nearest.of.{(Environment.GetEnvironmentVariable("FLY_APP_NAME") is { } app && app.StartsWith("ts-pr-", StringComparison.Ordinal) ? app : "ts-terrabase")}.internal:8081");

    /// <summary>
    /// The TerraBase project holding the platform's own databases. TerraBase seeds it with this fixed id on every start
    /// (<c>PlatformDatabases</c> in src/TerraBase); the two must stay equal.
    /// </summary>
    public const string StorageProject = "00000000000000000000000002";

    /// <summary>ts-auth's database in <see cref="StorageProject"/>; ts-auth creates its collections there itself.</summary>
    public const string StorageDatabase = "platform-auth";

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
        builder.Services.AddSingleton(_ => new HttpClient(new TerraBaseHttp2(TerraBaseHttp.CreateHandler())));
        // The nearest TerraBase machine's private listener: no credential, it trusts the private network.
        builder.Services.AddSingleton(services => new TerraBaseDocumentsClient(services.GetRequiredService<HttpClient>(), new TerraBaseDocumentsClientOptions
        {
            BaseAddress = TerraBaseOrigin,
        }).Database(StorageProject, StorageDatabase));
        builder.Services.AddSingleton<AuthStore>();
        builder.Services.AddSingleton<Users>();
        builder.Services.AddSingleton<Sessions>();
        builder.Services.AddSingleton<Passcodes>();
        builder.Services.AddSingleton<PasswordHasher>();
        builder.Services.AddSingleton<HeadlessAuth>();
        builder.Services.AddSingleton<TotpCredentials>();
        builder.Services.AddSingleton<PlatformDirectory>();
        builder.Services.AddSingleton<PlatformAuthentication>();
        builder.Services.AddSingleton<ApiKeys>();
        builder.Services.AddSingleton<PlatformGrants>();
        builder.Services.AddSingleton<Invitations>();
        builder.Services.AddHostedService<SessionPruner>();
        builder.Services.AddSingleton<TerraBaseStorageHealth>();
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
