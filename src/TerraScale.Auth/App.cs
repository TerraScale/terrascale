using Microsoft.AspNetCore.HttpOverrides;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Features.Email;
using TerraScale.Auth.Features.Headless;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Mfa;
using TerraScale.Auth.Features.Observability;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.Platform;
using TerraScale.Auth.Features.RateLimiting;
using TerraScale.Auth.Features.SignIn;
using TerraScale.Auth.Features.SigningKeys;
using TerraScale.Auth.Hosting;
using TerraScale.Auth.Store;
using TerraScale.DotNet.TerraBase;
using TerraScale.Endpoints;
using ZLogger;

namespace TerraScale.Auth;

/// <summary>Composes services, middleware and endpoints.</summary>
public static class App
{
    /// <summary>OpenAPI document title; clients key on it.</summary>
    public const string ApiTitle = "TerraScale Auth API";

    /// <summary>
    /// Builds the application for validated options. <paramref name="configure"/> lets
    /// integration tests replace infrastructure before the container is built.
    /// </summary>
    public static WebApplication Build(AuthOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(options.Listen));

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
        if (options.TerraScaleApiEndpoints.Count == 0)
        {
            builder.Services.AddSingleton(_ => new HttpClient(TerraBaseHttp.CreateHandler()));
            // The configured URL is an explicit static override. Replace the resolver with Atlas
            // discovery when ts-dotnet supplies it; every process must pin the same database authority.
            builder.Services.AddSingleton<ITerraBaseEndpointResolver>(_ => new TerraBaseStaticEndpointResolver(options.TerraScaleApiUrl));
        }
        else
        {
            // The cell's private members, in the same order on every replica (see TerraBaseMembers).
            builder.Services.AddSingleton<TerraBaseMembers>();
            builder.Services.AddHostedService(services => services.GetRequiredService<TerraBaseMembers>());
            builder.Services.AddSingleton(services => new HttpClient(services.GetRequiredService<TerraBaseMembers>().CreateHandler()));
            builder.Services.AddSingleton<ITerraBaseEndpointResolver>(services => services.GetRequiredService<TerraBaseMembers>());
        }
        builder.Services.AddSingleton(services => new TerraBaseDocumentsClient(services.GetRequiredService<HttpClient>(), new TerraBaseDocumentsClientOptions
        {
            EndpointResolver = services.GetRequiredService<ITerraBaseEndpointResolver>(),
            ApiKey = options.TerraScaleApiKey,
        }).Database(options.StorageProject, options.StorageDatabase));
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
        builder.Services.AddSingleton<ISigningKeyVault>(_ => options.SigningKeyVault is { } vault ? new OciSigningKeyVault(vault) : new UnconfiguredSigningKeyVault());
        builder.Services.AddSingleton<KeyRing>();
        builder.Services.AddSingleton<SigningKeyRotation>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SigningKeyRotation>());
        builder.Services.AddSingleton<AttemptLimiter>();
        if (options.SmtpHost is null)
        {
            builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }
        else
        {
            builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
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
        if (options.SmtpHost is null && !options.Development)
        {
            app.Logger.ZLogWarning($"TS_AUTH_SMTP_* is not configured: email is logged, not delivered");
        }

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
