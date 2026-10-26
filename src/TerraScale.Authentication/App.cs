using Microsoft.AspNetCore.HttpOverrides;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Observability;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Features.Sso;
using TerraScale.Authentication.Features.Ui;
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

    /// <summary>Service key for peer-only management calls to the regional TerraBase endpoint.</summary>
    public const string StorageAuthority = "terrabase-authority";

    /// <summary>
    /// The trusted regional storage endpoint, required outside Development. Reads and writes use this copy without
    /// remote fallback; replicated winner claims reconcile concurrent conditional credentials.
    /// Development alone may use the loopback default.
    /// </summary>
    public static Uri TerraBaseOrigin(AuthOptions options) => options.TerraBaseUrl
        ?? (options.Development ? new("http://127.0.0.1:8081")
            : throw new AuthConfigurationException("TS_AUTH_TERRABASE_URL is required outside Development"));

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
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddEnvironmentVariables();
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
        // All storage traffic uses the configured regional endpoint without remote fallback.
        builder.Services.AddSingleton(services => new HttpClient(RequestIdentity.Wrap(TerraBaseTransport.CreateHandler(
            [],
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TerraBaseTransport)), services.GetService<TerraBaseTransport.HostResolver>(),
            peerSecret: services.GetRequiredService<IConfiguration>()[TerraBaseTransport.PeerSecretVariable]),
            services.GetRequiredService<IConfiguration>()[TerraBaseTransport.PeerSecretVariable])));
        builder.Services.AddSingleton(services => new TerraBaseDocumentsClient(services.GetRequiredService<HttpClient>(), new TerraBaseDocumentsClientOptions
        {
            BaseAddress = TerraBaseOrigin(options),
        }).Database(StorageProject, StorageDatabase));
        builder.Services.AddSingleton(services => new AuthStore(services.GetRequiredService<TerraBaseDatabase>(), services.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<Users>();
        builder.Services.AddSingleton<Sessions>();
        builder.Services.AddSingleton<Passcodes>();
        builder.Services.AddSingleton<PasswordHasher>();
        builder.Services.AddSingleton<HostedAuth>();
        builder.Services.AddSingleton<TotpCredentials>();
        builder.Services.AddSingleton<PlatformDirectory>();
        builder.Services.AddSingleton<AccountDeletion>();
        // Shared-history erasure and project closure reach TerraBase through the regional peer transport, acting as no user.
        builder.Services.AddKeyedSingleton(StorageAuthority, (services, _) => new TerraBaseManagementClient(services.GetRequiredService<HttpClient>(),
            new TerraBaseManagementClientOptions { BaseAddress = TerraBaseOrigin(options) }));
        builder.Services.AddSingleton<Closures>();
        builder.Services.AddSingleton<ClosureWorker>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ClosureWorker>());
        builder.Services.AddSingleton<DeletionCleanup>();
        builder.Services.AddHostedService(services => services.GetRequiredService<DeletionCleanup>());
        builder.Services.AddSingleton<PlatformBootstrap>();
        builder.Services.AddSingleton<PlatformAuthentication>();
        builder.Services.AddSingleton<ApiKeys>();
        builder.Services.AddSingleton<PlatformGrants>();
        builder.Services.AddSingleton<EndUserAccessTokens>();
        builder.Services.AddHttpClient(GitHubActionsGrant.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton<GitHubActionsKeys>();
        builder.Services.AddSingleton<Invitations>();
        builder.Services.AddSingleton<TenantDirectory>();
        builder.Services.AddSingleton<ProjectAuthSettings>();
        builder.Services.AddHttpClient(SsoConnections.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.MaxResponseContentBufferSize = 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(PublicAddresses.Handler);
        builder.Services.AddSingleton<SsoConnections>();
        builder.Services.AddSingleton<SsoSignIn>();
        builder.Services.AddHttpClient(SocialSignIn.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.MaxResponseContentBufferSize = 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(PublicAddresses.Handler);
        builder.Services.AddSingleton<SocialSignIn>();
        builder.Services.AddSingleton<SocialFlow>();
        builder.Services.AddSingleton<Features.Social.PlatformSocialSignIn>();
        builder.Services.AddHttpClient(Features.Social.PlatformSocialSignIn.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddHostedService<SessionPruner>();
        // Ready means this regional copy takes writes.
        builder.Services.AddSingleton(services => new TerraBaseStorageHealth(services.GetRequiredService<TerraBaseDatabase>()));
        builder.Services.AddSingleton<IStorageHealth, SigningKeyReadiness>();
        builder.Services.AddSingleton<ReadinessProbe>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ReadinessProbe>());
        builder.Services.AddSingleton<IDocumentStore, TerraBaseDocumentStore>();
        builder.Services.AddSingleton<KeyRing>();
        builder.Services.AddSingleton<SigningKeyRotation>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SigningKeyRotation>());
        builder.Services.AddSingleton(services => new AttemptLimiter(services.GetRequiredService<IDocumentStore>(), services.GetRequiredService<TimeProvider>(), options,
            services.GetRequiredService<IConfiguration>()["TS_REGION"] ?? (options.Development ? "gru" : throw new AuthConfigurationException("TS_REGION is required outside Development"))));
        builder.Services.AddSingleton<EmailAdmission>();
        if (options.CloudflareEmail is { } cloudflareEmail)
        {
            builder.Services.AddSingleton(cloudflareEmail);
            builder.Services.AddHttpClient<CloudflareEmailSender>(client => client.Timeout = TimeSpan.FromSeconds(30));
            builder.Services.AddTransient<IEmailSender, AdmittedEmailSender>();
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

        // TS-996: hosted auth serves only GRU. Regional LWW writes would lose concurrent mail charges.
        if (options.CloudflareEmail is not null && (builder.Configuration["TS_REGION"] ?? (options.Development ? "gru" : null)) != "gru")
        {
            throw new AuthConfigurationException("Shared email admission requires the GRU authentication authority");
        }

        // A named TerraBase requires a valid peer credential before the first storage request.
        if (options.TerraBaseUrl is not null
            && string.IsNullOrEmpty(builder.Configuration[TerraBaseTransport.PeerSecretVariable]))
        {
            throw new AuthConfigurationException($"{TerraBaseTransport.PeerSecretVariable} is required with TS_AUTH_TERRABASE_URL");
        }

        if (builder.Configuration[TerraBaseTransport.PeerSecretVariable] is { Length: > 0 } peerSecret)
        {
            _ = TerraBaseTransport.Validated(peerSecret);
        }

        // This pipeline owns forwarding order and trust, even when ASP.NET's automatic forwarding setting is enabled.
        builder.Configuration["ForwardedHeaders_Enabled"] = "false";

        var app = builder.Build();
        app.UseTerraScaleRequestIdentity(AuthStore.NewId, builder.Configuration[TerraBaseTransport.PeerSecretVariable]);
        // Resolved now, so a ts-auth without storage refuses to start (see TerraBaseOrigin); tests may register their own.
        app.Services.GetRequiredService<TerraBaseDatabase>();
        // Hashes its timing equalizer now, so the first password check of an unknown account costs no more than any other.
        app.Services.GetRequiredService<PasswordHasher>();
        if (options.CloudflareEmail is null && !options.Development)
        {
            app.Logger.ZLogWarning($"CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_EMAIL_TOKEN are not configured: email is logged, not delivered");
        }

        app.Use((HttpContext context, RequestDelegate next) =>
        {
            context.Response.OnStarting(() =>
            {
                BrowserSecurity.ApplyResponseHeaders(context.Response, options.PublicOrigin.Scheme == Uri.UriSchemeHttps);
                return Task.CompletedTask;
            });
            return next(context);
        });
        app.UseAuthMetrics();
        app.UseMiddleware<ErrorEnvelopeMiddleware>();
        // Inside the envelope, so a failing page still answers 500; only empty, unstarted responses reach the handler.
        app.UseStatusCodePages(NotFoundPage.HandleAsync);
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
        app.UseTenancy();
        app.UseHostedTheme();
        // OpenIddict answers discovery, JWKS and protocol errors here and validates passthrough requests first.
        app.UseAuthentication();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        return app;
    }
}
