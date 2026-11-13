using TerraScale.Authentication.Features.UserApiKeys;
using System.Net;
using TerraScale.Authentication.Features.AttackProtection;
using Fido2NetLib;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Http;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Impersonation;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Observability;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.ProjectOrganizations;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Features.Sso;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Features.WebAuthn;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet.TerraBase;
using TerraScale.Endpoints;

namespace TerraScale.Authentication;

/// <summary>Composes services, middleware and endpoints.</summary>
public static class App
{
    /// <summary>OpenAPI document title; clients key on it.</summary>
    public const string ApiTitle = "TerraScale Auth API";

    /// <summary>Service key for peer-only management calls to the regional TerraBase endpoint.</summary>
    public const string StorageAuthority = "terrabase-authority";

    /// <summary>Single-use proofs and shared mail budgets require GRU's one TerraBase writer in production.</summary>
    public static void ValidateAuthenticationAuthority(AuthOptions options, string? region)
    {
        ArgumentNullException.ThrowIfNull(options);
        if ((!options.Development || options.Smtp is not null || options.CloudflareEmail is not null) && (region ?? (options.Development ? "gru" : null)) != "gru")
        {
            throw new AuthConfigurationException("Authentication requires the GRU authority for durable single-use proofs and email admission");
        }
    }

    /// <summary>
    /// The passkey relying party: the public origin's registrable host, so ceremonies from every allowed origin
    /// (auth and the dashboard) bind to one account-scoped credential. Hosts of two labels are already registrable.
    /// </summary>
    public static string PasskeyRpId(string host)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        var labels = host.Split('.');
        return labels.Length >= 3 ? string.Join(".", labels, 1, labels.Length - 1) : host;
    }

    /// <summary>Whether an allowed origin's host is the relying party itself or one of its subdomains.</summary>
    public static bool PasskeyOriginMatches(string origin, string rpId)
    {
        ArgumentException.ThrowIfNullOrEmpty(origin);
        ArgumentException.ThrowIfNullOrEmpty(rpId);
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Host.Equals(rpId, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + rpId, StringComparison.OrdinalIgnoreCase));
    }

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

    /// <summary>ts-auth's database in <see cref="StorageProject"/>; ts-auth creates its tables there itself.</summary>
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
        builder.Services.AddSingleton(TimeProvider.System);
        // All storage traffic uses the configured regional endpoint without remote fallback.
        builder.Services.AddSingleton(services => new HttpClient(StorageTiming.Wrap(RequestIdentity.Wrap(TerraBaseTransport.CreateHandler(
            [],
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TerraBaseTransport)), services.GetService<TerraBaseTransport.HostResolver>(),
            peerSecret: services.GetRequiredService<IConfiguration>()[TerraBaseTransport.PeerSecretVariable],
            caller: services.GetRequiredService<IConfiguration>()["TS_AUTH_TERRABASE_KEY_FILE"] is { Length: > 0 } path
                ? new CallerCredential("ts-auth", File.ReadAllText(path), services.GetRequiredService<TimeProvider>()) : null),
            services.GetRequiredService<IConfiguration>()[TerraBaseTransport.PeerSecretVariable]))));
        builder.Services.AddSingleton(services => new TerraBaseSqlClient(services.GetRequiredService<HttpClient>(), new TerraBaseSqlClientOptions
        {
            BaseAddress = TerraBaseOrigin(options),
        }).Database(StorageProject, StorageDatabase));
        builder.Services.AddPooledDbContextFactory<AuthDbContext>((services, database) => database
            .UseTerraBase(services.GetRequiredService<TerraBaseSqlDatabase>())
            .UseLoggerFactory(new ConflictLogging(services.GetRequiredService<ILoggerFactory>()))
            .ConfigureWarnings(warnings => warnings.Log((RelationalEventId.CommandError, LogLevel.Debug)))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
        builder.Services.AddSingleton<AuthData>();
        builder.Services.AddSingleton<AuthSchema>();
        builder.Services.AddSingleton<Credentials>();
        builder.Services.AddSingleton<Challenges>();
        builder.Services.AddSingleton<Users>();
        builder.Services.AddSingleton<UserProfiles>();
        builder.Services.AddSingleton<Impersonations>();
        builder.Services.AddHostedService<ImpersonationExpiry>();
        builder.Services.AddSingleton<WebhookOutbox>();
        builder.Services.AddSingleton<ProjectOrganizationDirectory>();
        builder.Services.AddHostedService<ProjectOrganizationDomainWorker>();
        builder.Services.AddSingleton<Sessions>();
        builder.Services.AddSingleton<Passcodes>();
        builder.Services.AddSingleton(new TurnstileKeys(Environment.GetEnvironmentVariable("TS_AUTH_TURNSTILE_SITE_KEY"),
            Environment.GetEnvironmentVariable("TS_AUTH_TURNSTILE_SECRET_KEY")));
        builder.Services.AddSingleton<AttackProtectionHttp>();
        builder.Services.AddSingleton<PasswordProtection>();
        builder.Services.AddSingleton<AccountProtection>();
        builder.Services.AddHttpClient(AttackProtectionHttp.ClientName, client => client.Timeout = TimeSpan.FromSeconds(10)).RemoveAllLoggers();
        builder.Services.AddSingleton<PasswordHasher>();
        builder.Services.AddSingleton<HostedAuth>();
        builder.Services.AddSingleton<TotpCredentials>();
        builder.Services.AddSingleton<PasskeyCredentials>();
        var passkeyOrigins = new[] { options.PublicOrigin.AbsoluteUri.TrimEnd('/'), AuthOptions.DashboardOrigin };
        var passkeyRpId = PasskeyRpId(options.PublicOrigin.Host);
        if (!options.Development)
        {
            foreach (var origin in passkeyOrigins.Where(origin => !PasskeyOriginMatches(origin, passkeyRpId)))
            {
                throw new AuthConfigurationException($"The passkey origin {origin} must be the relying party {passkeyRpId} or a subdomain of it.");
            }
        }

        builder.Services.AddSingleton(new Fido2NetLib.Fido2(new Fido2Configuration
        {
            RPID = passkeyRpId,
            RPName = "TerraScale",
            Origins = new HashSet<string>(passkeyOrigins, StringComparer.Ordinal),
        }));
        builder.Services.AddSingleton<PlatformDirectory>();
        builder.Services.AddSingleton<AccountDeletion>();
        // Project closure reaches TerraBase through the regional peer transport, acting as no user.
        builder.Services.AddKeyedSingleton(StorageAuthority, (services, _) => new TerraBaseManagementClient(services.GetRequiredService<HttpClient>(),
            new TerraBaseManagementClientOptions { BaseAddress = TerraBaseOrigin(options) }));
        builder.Services.AddSingleton<Closures>();
        builder.Services.AddSingleton<ClosureWorker>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ClosureWorker>());
        builder.Services.AddSingleton<DeletionCleanup>();
        builder.Services.AddHostedService(services => services.GetRequiredService<DeletionCleanup>());
        builder.Services.AddSingleton<PlatformBootstrap>();
        builder.Services.AddSingleton<PlatformAuthentication>();
        builder.Services.AddSingleton<StepUpProofs>();
        builder.Services.AddSingleton<EmailChanges>();
        builder.Services.AddSingleton<ApiKeys>();
        builder.Services.AddSingleton<UserApiKeys>();
        builder.Services.AddSingleton<PlatformGrants>();
        builder.Services.AddSingleton<EndUserAccessTokens>();
        // Singletons keep factory clients (discovery and key caches), so a rotating handler could never be collected:
        // its expiry timer would root the whole container long after the host stops. Handlers live as long as the
        // container instead, and pooled connections still re-resolve DNS.
        builder.Services.ConfigureHttpClientDefaults(http => http
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = PublicAddresses.ConnectionLifetime }));
        builder.Services.AddHttpClient(GitHubActionsGrant.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton<GitHubActionsKeys>();
        builder.Services.AddSingleton<Invitations>();
        builder.Services.AddSingleton<TenantDirectory>();
        builder.Services.AddSingleton<ProjectAuthSettings>();
        builder.Services.AddSingleton<SignUpRestrictions>();
        builder.Services.AddSingleton<ProjectInvitations>();
        builder.Services.AddSingleton<WebhookEndpoints>();
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
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = PublicAddresses.ConnectionLifetime });
        builder.Services.AddWebhookDelivery();
        builder.Services.AddSingleton<WebhookHistory>();
        builder.Services.AddSingleton<WebhookNotifications>();
        builder.Services.AddHostedService<WebhookExpansionWorker>();
        builder.Services.AddHostedService<WebhookDeliveryWorker>();
        builder.Services.AddHostedService<SessionPruner>();
        // Ready means this regional copy takes writes.
        builder.Services.AddSingleton<TerraBaseStorageHealth>();
        builder.Services.AddSingleton<IStorageHealth, SigningKeyReadiness>();
        builder.Services.AddSingleton<ReadinessProbe>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ReadinessProbe>());
        builder.Services.AddSingleton<IAttemptStore, TerraBaseAttemptStore>();
        builder.Services.AddSingleton<KeyRing>();
        builder.Services.AddSingleton<SigningKeyRotation>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SigningKeyRotation>());
        builder.Services.AddSingleton(services => new AttemptLimiter(services.GetRequiredService<IAttemptStore>(), services.GetRequiredService<TimeProvider>(), options,
            services.GetRequiredService<IConfiguration>()["TS_REGION"] ?? (options.Development ? "gru" : throw new AuthConfigurationException("TS_REGION is required outside Development")),
            services.GetRequiredService<ILogger<AttemptLimiter>>()));
        builder.Services.AddSingleton<EmailAdmission>();
        if (options.Smtp is { } smtp)
        {
            builder.Services.AddSingleton(smtp);
            builder.Services.AddSingleton<SmtpEmailSender>();
        }

        if (options.CloudflareEmail is { } cloudflareEmail)
        {
            builder.Services.AddSingleton(cloudflareEmail);
            builder.Services.AddHttpClient<CloudflareEmailSender>(client => client.Timeout = TimeSpan.FromSeconds(30));
        }

        if (options.Smtp is not null || options.CloudflareEmail is not null)
        {
            builder.Services.AddSingleton(provider =>
            {
                var providers = new List<IEmailProvider>();
                if (options.Smtp is not null)
                {
                    providers.Add(provider.GetRequiredService<SmtpEmailSender>());
                }

                if (options.CloudflareEmail is not null)
                {
                    providers.Add(provider.GetRequiredService<CloudflareEmailSender>());
                }

                return new FailoverEmailSender(providers, provider.GetRequiredService<ILogger<FailoverEmailSender>>());
            });
            builder.Services.AddTransient<IEmailSender, AdmittedEmailSender>();
        }
        else
        {
            builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }

        builder.Services.AddSingleton<AuthEmails>();
        builder.Services.AddSingleton<EmailSettings>();
        builder.Services.AddSingleton<OwnerDeliveries>();
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

        ValidateAuthenticationAuthority(options, builder.Configuration["TS_REGION"]);

        // ts-orch (which sets TS_NODE) serves every node through its edge on loopback, which replaces X-Forwarded-For
        // with the client's address. Without trusting it, every end user shares the edge's IP budgets.
        if (builder.Configuration["TS_NODE"] is { Length: > 0 })
        {
            options = options with { TrustedProxies = [.. options.TrustedProxies.Union([IPAddress.Loopback, IPAddress.IPv6Loopback])] };
            builder.Services.AddSingleton(options);
        }

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

        _ = TerraBaseOrigin(options);
        if (!options.Development && (options.Smtp is null || options.CloudflareEmail is null))
        {
            throw new AuthConfigurationException("TS_AUTH_SMTP_HOST, TS_AUTH_SMTP_USERNAME, TS_AUTH_SMTP_PASSWORD, CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_EMAIL_TOKEN are required outside Development");
        }

        var app = builder.Build();
        app.UseTerraScaleRequestIdentity(AuthIds.New, builder.Configuration[TerraBaseTransport.PeerSecretVariable]);
        // Resolved now, so a ts-auth without storage refuses to start (see TerraBaseOrigin); tests may register their own.
        app.Services.GetRequiredService<TerraBaseSqlDatabase>();
        // Hashes its timing equalizer now, so the first password check of an unknown account costs no more than any other.
        app.Services.GetRequiredService<PasswordHasher>();
        app.Use((HttpContext context, RequestDelegate next) =>
        {
            context.Response.OnStarting(() =>
            {
                BrowserSecurity.ApplyResponseHeaders(context.Response, options.PublicOrigin.Scheme == Uri.UriSchemeHttps);
                if (context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is UserInfoEndpoint.RouteName or UserInfoEndpoint.PostRouteName)
                {
                    // Registered before authentication; survives protocol failures and the error envelope's Clear().
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers.Pragma = "no-cache";
                }

                return Task.CompletedTask;
            });
            return next(context);
        });
        app.UseAuthMetrics();
        StorageTiming.Use(app);
        app.UseMiddleware<ErrorEnvelopeMiddleware>();
        // Inside the envelope, so a failing page still answers 500; only empty, unstarted responses reach the handler.
        app.UseStatusCodePages(NotFoundPage.HandleAsync);
        // Ahead of the rate-limit guards: their storage writes stall with every other write.
        app.UseMiddleware<TokenEndpointDeadline>();
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
        app.UseBotChallenge();
        app.UseImpersonation();
        // OpenIddict answers discovery, JWKS and protocol errors here and validates passthrough requests first.
        app.UseAuthentication();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        return app;
    }
}
