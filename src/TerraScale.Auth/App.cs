using TerraScale.Auth.Configuration;
using TerraScale.Auth.Features.Email;
using TerraScale.Auth.Features.Headless;
using TerraScale.Auth.Features.Identity;
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

namespace TerraScale.Auth;

/// <summary>Composes services, middleware and endpoints.</summary>
public static partial class App
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
        builder.Services.AddSingleton<PlatformDirectory>();
        builder.Services.AddSingleton<PlatformAuthentication>();
        builder.Services.AddHostedService<SessionPruner>();
        builder.Services.AddSingleton<IStorageHealth, TerraBaseStorageHealth>();
        builder.Services.AddSingleton<ReadinessProbe>();
        builder.Services.AddSingleton<IDocumentStore, TerraBaseDocumentStore>();
        builder.Services.AddSingleton<KeyRing>();
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

        var app = builder.Build();
        // Hashes its timing equalizer now, so the first password check of an unknown account costs no more than any other.
        app.Services.GetRequiredService<PasswordHasher>();
        if (options.SmtpHost is null && !options.Development)
        {
            LogEmailNotConfigured(app.Logger);
        }

        app.UseMiddleware<ErrorEnvelopeMiddleware>();
        // OpenIddict answers discovery, JWKS and protocol errors here and validates passthrough requests first.
        app.UseAuthentication();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        return app;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "TS_AUTH_SMTP_* is not configured: email is logged, not delivered")]
    private static partial void LogEmailNotConfigured(ILogger logger);
}
