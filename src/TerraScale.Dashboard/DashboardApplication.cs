using TerraScale.Dashboard.Platform.Observability;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Features.Imports;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Features.Support;
using TerraScale.Dashboard.Features.Organizations;
using TerraScale.Dashboard.Features.AuthenticationManagement;
using TerraScale.Dashboard.Features.Admin;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Usage;
using TerraScale.Dashboard.Features.Settings;
using TerraScale.Dashboard.Features.QuerySubscriptions;
using TerraScale.Dashboard.Features.FileStorage;
using TerraScale.Dashboard.Platform.Assets;
using TerraScale.Dashboard.Platform.Development;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Platform.Contracts;
using TerraScale.Dashboard.Shared.UI;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard;

public static class DashboardApplication
{
    // The service override is for isolated test hosts; production has one service graph.
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configureTestHost = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = "web/static"
        });
        // The console formatter's fixed setting retains sanitized request/trace scopes in structured timings.
        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
        builder.Services.AddSafeHttpLogging();
        builder.Services.AddSingleton<AssetUrls>();
        builder.Services.AddResponseCompression(options =>
        {
            // ASP.NET's required third-party setting: the loopback TLS edge forwards HTTPS requests.
            options.EnableForHttps = true;
        });
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = problem =>
            problem.ProblemDetails.Extensions["requestId"] = problem.HttpContext.TraceIdentifier);
        builder.Services.AddSingleton<ServiceSloTelemetry>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IDashboardIdentity, DashboardIdentity>();
        builder.Services.AddSingleton<IDashboardState, TerraBaseDashboardState>();
        builder.Services.AddSingleton<TerraBasePlanSync>();
        builder.Services.AddHostedService<DashboardStateInitializer>();
        builder.Services.AddSingleton<DependencyReadiness>();
        builder.Services.AddSingleton<DashboardSessions>();
        builder.Services.AddAccountFeatures();
        builder.Services.AddAllowanceNotifications();
        builder.Services.AddSupport();
        builder.Services.AddCustomerDelivery();
        builder.Services.AddAlerts();
        builder.Services.AddAuth();
        builder.Services.AddDatabaseFeatures();
        builder.Services.AddImportFeatures();
        builder.Services.AddOnboarding();
        builder.Services.AddBillingFeatures();
        builder.Services.AddHomeFeatures();
        builder.Services.AddAnalyticsFeatures();
        builder.Services.AddUsageFeatures();
        builder.Services.AddSettingsFeatures();
        builder.Services.AddQuerySubscriptions();
        builder.Services.AddFileStorage();
        builder.Services.AddOrganizationFeatures();
        builder.Services.AddSingleton<AccessTokens>();
        builder.Services.AddSingleton<IProfileTokens>(services => services.GetRequiredService<AccessTokens>());
        builder.Services.AddDashboardMcp();
        builder.Services.AddDashboardContracts();
        builder.Services.AddTransient<ServerTiming.Handler>();
        builder.Services.AddTransient<RequestIdentity.Handler>();
        builder.Services.AddHttpClient("platform", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(TerraBaseHttp.CreateHandler)
            .AddHttpMessageHandler<ServerTiming.Handler>()
            .AddHttpMessageHandler<RequestIdentity.Handler>();
        builder.Services.AddTransient<TerraBasePeerCredential>();
        builder.Services.AddHttpClient(TerraBaseDashboardState.ClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(TerraBaseHttp.CreateHandler)
            .AddHttpMessageHandler<ServerTiming.Handler>()
            .AddHttpMessageHandler<TerraBasePeerCredential>();
        configureTestHost?.Invoke(builder);
        var app = builder.Build();
        RazorSliceWarmup.EnsureWarmed();

        var slo = app.Services.GetRequiredService<ServiceSloTelemetry>();
        var privateScrapeKey = new object();
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/metrics"))
                context.Items[privateScrapeKey] = ServiceSloTelemetry.PrivateScrape(context);
            return slo.ObserveAsync(context, next);
        });

        // ts-orch's TLS edge on the same host proxies over loopback. Only loopback peers (the default known
        // proxies) may set the scheme and client IP; a browser's own forwarding headers never reach past the edge.
        app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
        var timingLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ServerTiming");
        app.Use((context, next) => RequestIdentity.MiddlewareAsync(context, next, timingLogger));
        app.Use((context, next) => ServerTiming.MiddlewareAsync(context, next, timingLogger));
        app.Use(async (context, next) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            context.Items["nonce"] = nonce;
            var path = context.Request.Path.Value ?? "";
            var editorStyles = path.StartsWith("/api/v1/dashboard/data/", StringComparison.Ordinal) ||
                path.StartsWith("/api/v1/dashboard/indexes", StringComparison.Ordinal) ||
                path.StartsWith("/databases/", StringComparison.Ordinal) &&
                (path.Contains("/documents", StringComparison.Ordinal) || path.EndsWith("/sql", StringComparison.Ordinal) || path.EndsWith("/schema", StringComparison.Ordinal));
            var reference = context.Request.Path.StartsWithSegments("/reference");
            var fileStorage = path == "/storage" || path.StartsWith("/api/v1/dashboard/storage/", StringComparison.Ordinal);
            var fileOrigins = fileStorage ? " https://*.r2.cloudflarestorage.com" : "";
            var styles = reference ? $"style-src 'self' 'nonce-{nonce}'; style-src-attr 'unsafe-inline'" :
                editorStyles ? $"style-src 'self' 'nonce-{nonce}'; style-src-attr 'unsafe-inline'; worker-src 'self'" : "style-src 'self'";
            // form-action covers the redirects that follow forms: sign-out to ts-auth, checkout and portal to Stripe,
            // and an MCP consent's return to the client's registered redirect URI.
            string Policy() =>
                $"default-src 'none'; script-src 'self' 'nonce-{nonce}'; {(context.Items.ContainsKey("auth-branding") ? $"style-src 'self' 'nonce-{nonce}'" : styles)}; " +
                $"img-src 'self' data:{(context.Items.ContainsKey("auth-branding") ? " https:" : "")}; font-src 'self'; connect-src 'self'" + fileOrigins + "; form-action 'self' https://auth.terrascale.tech https://checkout.stripe.com https://billing.stripe.com" + fileOrigins +
                (context.Items[Platform.Mcp.McpOAuth.FormActionKey] is string client ? " " + client : "") + "; base-uri 'none'; frame-ancestors 'none'";
            context.Response.OnStarting(() =>
            {
                // Error handling can clear headers; preserve endpoint policies and restore the baseline before writing.
                var headers = context.Response.Headers;
                if (headers.Location is { Count: 1 } && headers.Location[0] is { } location)
                    headers.Location = WorkspaceUrl.PreserveRequest(context, location);
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers.TryAdd("Referrer-Policy", "same-origin");
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                if (context.Request.IsHttps)
                    headers.StrictTransportSecurity = "max-age=31536000";
                headers.TryAdd("Content-Security-Policy", Policy());
                if (!context.Request.Path.StartsWithSegments("/js") &&
                    !context.Request.Path.StartsWithSegments("/css") &&
                    !context.Request.Path.StartsWithSegments("/fonts") &&
                    !context.Request.Path.StartsWithSegments("/brand") &&
                    !context.Request.Path.StartsWithSegments("/flags") &&
                    !context.Request.Path.StartsWithSegments("/img"))
                    headers.TryAdd("Cache-Control", "no-store");
                return Task.CompletedTask;
            });
            try
            {
                await next(context);
            }
            catch (Exception error) when (!context.Response.HasStarted &&
                                          (error is OrganizationMfaRequiredException || error is AccountAccessException { Code: TsAuthGateway.MfaRequiredCode }))
            {
                // An organization that requires two-factor blocks a member without it with a page, never a failure.
                var blocked = error as OrganizationMfaRequiredException;
                var message = blocked?.Message ?? "This organization requires two-factor authentication.";
                if (AcceptsJson(context))
                {
                    await Results.Problem(message, statusCode: StatusCodes.Status403Forbidden, extensions: new Dictionary<string, object?>
                    { ["code"] = TsAuthGateway.MfaRequiredCode, ["manageUrl"] = TsAuthGateway.ManageMfaUrl }).ExecuteAsync(context);
                    return;
                }
                var view = MfaRequiredView.Create(new(new DocumentModel("Two-factor required", nonce),
                    blocked?.OrganizationName ?? "This organization", Csrf.Token(context), blocked?.Others ?? []));
                view.StatusCode = StatusCodes.Status403Forbidden;
                await ((IResult)view).ExecuteAsync(context);
            }
            catch (AccountAccessException error)
            {
                if (context.Response.HasStarted) throw;
                await RequestError.Response(context, error.StatusCode, error.Message).ExecuteAsync(context);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or TimeoutException or Stripe.StripeException ||
                                          error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
            {
                // Platform failures never select a demo store or leak response bodies/secrets.
                app.Logger.LogWarning("Dashboard dependency failed ({ErrorType})", error.GetType().Name);
                if (context.Response.HasStarted) throw;
                if (AcceptsJson(context))
                {
                    await Results.Problem("A TerraScale service is temporarily unavailable. Try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
                    return;
                }
                var view = Shared.UI.Slices.ServiceUnavailable.Create(new DocumentModel("Service unavailable", nonce));
                view.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ((IResult)view).ExecuteAsync(context);
            }
            catch (Exception error) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            {
                app.Logger.LogError("Dashboard request failed ({ErrorType})", error.GetType().Name);
                context.Response.Clear();
                await RequestError.Response(context, StatusCodes.Status500InternalServerError).ExecuteAsync(context);
            }
        });
        app.Use((context, next) => context.Request.Path.StartsWithSegments("/metrics")
            ? slo.ScrapeAsync(context, context.Request.Path == "/metrics" && HttpMethods.IsGet(context.Request.Method) && context.Items[privateScrapeKey] is true)
            : next(context));
        // Browser forms and API clients both get an actionable body for otherwise empty failures.
        app.UseStatusCodePages(status => RequestError.Response(status.HttpContext,
            status.HttpContext.Response.StatusCode).ExecuteAsync(status.HttpContext));
        // Compress public assets only; signed-in HTML, secrets and streaming responses keep their own delivery.
        app.UseWhen(context => context.Request.Path.StartsWithSegments("/css") || context.Request.Path.StartsWithSegments("/js") ||
            context.Request.Path.StartsWithSegments("/brand") || context.Request.Path.StartsWithSegments("/flags"),
            assets => assets.UseResponseCompression());
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = response =>
            {
                var assets = response.Context.RequestServices.GetRequiredService<AssetUrls>();
                var version = assets.Version(response.Context.Request.Path.Value ?? "");
                response.Context.Response.Headers.CacheControl =
                    version == response.Context.Request.Query["v"] && version.Length != 0
                        ? "public,max-age=31536000,immutable"
                        : "public,max-age=0,must-revalidate";
            }
        });
        app.MapGet("/healthz", () => Results.Text("ok"));
        // ts-orch probes /readyz and ts-status probes /health/ready; both name the failing dependency. Public
        // bodies never carry the build revision.
        app.MapGet("/readyz", (DependencyReadiness readiness, CancellationToken cancellationToken) => readiness.CheckAsync(cancellationToken));
        app.MapGet("/health/ready", (DependencyReadiness readiness, CancellationToken cancellationToken) => readiness.CheckAsync(cancellationToken));
        app.MapGet("/health/live", (DependencyReadiness readiness) => Results.Json(new { status = "ok", region = readiness.Region }));
        app.MapAuth();
        app.MapAccountFeatures();
        app.MapOnboarding();
        app.MapDatabaseFeatures();
        app.MapImports();
        app.MapAuthenticationManagement();
        app.MapAdmin();
        app.MapHome();
        app.MapAnalytics();
        app.MapUsage();
        app.MapSettings();
        app.MapQuerySubscriptions();
        app.MapFileStorage();
        app.MapProfile();
        app.MapProfileAccessTokens();
        app.MapSupport();
        app.MapNotifications();
        app.MapAlerts();
        app.MapOrganizations();
        app.MapBilling();
        app.MapBillingPlanChanges();
        app.MapMcpOAuth();
        app.MapDashboardMcp();
        app.MapDevelopmentUpdates();
        app.MapDashboardContracts();
        app.MapFallback(async (HttpContext context, DashboardSessions sessions, AccountService accounts) =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            if (context.Request.Method != "GET" || context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Headers.Accept.Any(value => value is not null && (value.Contains("application/json") || value.Contains("text/event-stream"))) ||
                context.Request.Headers.ContainsKey("Datastar-Request"))
                return (IResult)Results.NotFound();
            return await Features.NotFound.NotFoundEndpoints.PageAsync(context, sessions, accounts);
        });
        return app;
    }

    private static bool AcceptsJson(HttpContext context) => RequestError.WantsJson(context);
}
