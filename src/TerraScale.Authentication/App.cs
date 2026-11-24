using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.Agents;
using TerraScale.Authentication.Features.UserApiKeys;
using System.Net;
using TerraScale.Authentication.Features.AttackProtection;
using Fido2NetLib;
using Microsoft.AspNetCore.HttpOverrides;
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
using TerraScale.Authentication.Storage.Schema;
using TerraScale.DotNet.TerraBase;
using TerraScale.Endpoints;

namespace TerraScale.Authentication;

public static class App
{
    public const string ApiTitle = "TerraScale Auth API";

    public const string StorageAuthority = "terrabase-authority";

    public static void ValidateAuthenticationAuthority(AuthOptions options, string? region)
    {
        ArgumentNullException.ThrowIfNull(options);
        if ((!options.Development || options.Smtp is not null || options.CloudflareEmail is not null) && (region ?? (options.Development ? "gru" : null)) != "gru")
        {
            throw new AuthConfigurationException("Authentication requires the GRU authority for durable single-use proofs and email admission");
        }
    }

    public static string PasskeyRpId(string host)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        var labels = host.Split('.');
        return labels.Length >= 3 ? string.Join(".", labels, 1, labels.Length - 1) : host;
    }

    public static bool PasskeyOriginMatches(string origin, string rpId)
    {
        ArgumentException.ThrowIfNullOrEmpty(origin);
        ArgumentException.ThrowIfNullOrEmpty(rpId);
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Host.Equals(rpId, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + rpId, StringComparison.OrdinalIgnoreCase));
    }

    public static Uri TerraBaseOrigin(AuthOptions options) => options.TerraBaseUrl
        ?? (options.Development ? new("http://127.0.0.1:8081")
            : throw new AuthConfigurationException("TS_AUTH_TERRABASE_URL is required outside Development"));

    public const string StorageProject = "00000000000000000000000002";

    public const string StorageDatabase = "ts-auth";

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
        builder.Services.AddSingleton(services =>
        {
            var origin = TerraBaseOrigin(options);
            var clientOrigin = TerraBaseTransport.ClientOrigin(origin);
            var internalTarget = clientOrigin == origin ? null : new DnsEndPoint(origin.DnsSafeHost, origin.Port);
            return new HttpClient(StorageTiming.Wrap(RequestIdentity.Wrap(TerraBaseTransport.CreateHandler(
                [],
                services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TerraBaseTransport)), services.GetService<TerraBaseTransport.HostResolver>(),
                peerSecret: services.GetRequiredService<IConfiguration>()[TerraBaseTransport.PeerSecretVariable],
                caller: services.GetRequiredService<IConfiguration>()["TS_AUTH_TERRABASE_KEY_FILE"] is { Length: > 0 } path
                    ? new CallerCredential("ts-auth", File.ReadAllText(path), services.GetRequiredService<TimeProvider>()) : null,
                internalTarget: internalTarget),
                services.GetRequiredService<IConfiguration>()[TerraBaseTransport.PeerSecretVariable])));
        });
        builder.Services.AddSingleton(services => new TerraBaseSqlClient(services.GetRequiredService<HttpClient>(), new TerraBaseSqlClientOptions
        {
            BaseAddress = TerraBaseTransport.ClientOrigin(TerraBaseOrigin(options)),
        }).Database(StorageProject, StorageDatabase));
        builder.Services.AddSingleton<AuthConnectionFactory>();
        builder.Services.AddSingleton<AuthData>();
        builder.Services.AddSingleton<TerraBaseDbUpRunner>();
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
        builder.Services.AddKeyedSingleton(StorageAuthority, (services, _) => new TerraBaseManagementClient(services.GetRequiredService<HttpClient>(),
            new TerraBaseManagementClientOptions { BaseAddress = TerraBaseTransport.ClientOrigin(TerraBaseOrigin(options)) }));
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
        builder.Services.AddSingleton<AgentLifecycleEvents>();
        builder.Services.AddSingleton<AgentDirectory>();
        builder.Services.AddSingleton<AgentAssertions>();
        builder.Services.AddSingleton<AgentTokens>();
        builder.Services.AddSingleton<AgentIdTransactions>();
        builder.Services.AddSingleton<AgentIdClients>();
        builder.Services.AddSingleton<LiveAgents>();
        builder.Services.AddSingleton<AgentExchange>();
        builder.Services.AddSingleton<AgentEnrollments>();
        builder.Services.AddSingleton<PlatformGrants>();
        builder.Services.AddSingleton<CustomTokens>();
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
        builder.Services.AddHostedService<AuthSeriesHost>();

        configure?.Invoke(builder);

        ValidateAuthenticationAuthority(options, builder.Configuration["TS_REGION"]);

        if (builder.Configuration["TS_NODE"] is { Length: > 0 })
        {
            options = options with { TrustedProxies = [.. options.TrustedProxies.Union([IPAddress.Loopback, IPAddress.IPv6Loopback])] };
            builder.Services.AddSingleton(options);
        }

        if (options.TrustedProxies.Count > 0 || options.TrustedProxyNetworks.Count > 0)
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

                foreach (var network in options.TrustedProxyNetworks)
                {
                    forwarding.KnownIPNetworks.Add(network);
                }
            });
        }

        if (options.TerraBaseUrl is not null
            && string.IsNullOrEmpty(builder.Configuration[TerraBaseTransport.PeerSecretVariable]))
        {
            throw new AuthConfigurationException($"{TerraBaseTransport.PeerSecretVariable} is required with TS_AUTH_TERRABASE_URL");
        }

        if (builder.Configuration[TerraBaseTransport.PeerSecretVariable] is { Length: > 0 } peerSecret)
        {
            _ = TerraBaseTransport.Validated(peerSecret);
        }

        builder.Configuration["ForwardedHeaders_Enabled"] = "false";

        _ = TerraBaseOrigin(options);
        if (!options.Development && (options.Smtp is null || options.CloudflareEmail is null))
        {
            throw new AuthConfigurationException("TS_AUTH_SMTP_HOST, TS_AUTH_SMTP_USERNAME, TS_AUTH_SMTP_PASSWORD, CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_EMAIL_TOKEN are required outside Development");
        }

        var app = builder.Build();
        app.UseTerraScaleRequestIdentity(AuthIds.New, builder.Configuration[TerraBaseTransport.PeerSecretVariable]);
        app.Services.GetRequiredService<TerraBaseSqlDatabase>();
        app.Services.GetRequiredService<PasswordHasher>();
        app.Use((HttpContext context, RequestDelegate next) =>
        {
            context.Response.OnStarting(() =>
            {
                BrowserSecurity.ApplyResponseHeaders(context.Response, options.PublicOrigin.Scheme == Uri.UriSchemeHttps);
                if (context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is UserInfoEndpoint.RouteName or UserInfoEndpoint.PostRouteName
                    or AgentIdUserInfoEndpoint.RouteName or AgentIdUserInfoEndpoint.PostRouteName)
                {
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
        app.UseStatusCodePages(NotFoundPage.HandleAsync);
        app.UseMiddleware<TokenEndpointDeadline>();
        TokenRateLimitMiddleware.UsePeerGuard(app);
        if (options.TrustedProxies.Count > 0 || options.TrustedProxyNetworks.Count > 0)
        {
            app.Use((HttpContext context, RequestDelegate next) =>
            {
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
        app.UseClientCors();
        app.UseHostedTheme();
        app.UseBotChallenge();
        app.UseImpersonation();
        app.UseAuthentication();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        return app;
    }
}
