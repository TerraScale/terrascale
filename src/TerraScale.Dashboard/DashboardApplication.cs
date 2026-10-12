using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Features.Organizations;
using TerraScale.Dashboard.Features.AuthenticationManagement;
using TerraScale.Dashboard.Features.Admin;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Usage;
using TerraScale.Dashboard.Features.Settings;
using TerraScale.Dashboard.Features.QuerySubscriptions;
using TerraScale.Dashboard.Platform.Assets;
using TerraScale.Dashboard.Platform.Development;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.UI;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard;

public static class DashboardApplication
{
    // <version>+<commit>; the image build passes the commit as SourceRevisionId.
    private static readonly string BuildVersion = typeof(DashboardApplication).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    // The service override is for isolated test hosts; production has one service graph.
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configureTestHost = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = "web/static"
        });
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
        builder.Services.AddSingleton<AssetUrls>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IDashboardIdentity, DashboardIdentity>();
        builder.Services.AddSingleton<IAuthGateway, TsAuthGateway>();
        builder.Services.AddSingleton<IDashboardState, TerraBaseDashboardState>();
        builder.Services.AddHostedService<DashboardStateInitializer>();
        builder.Services.AddSingleton<DashboardSessions>();
        builder.Services.AddAccountFeatures();
        builder.Services.AddEnrollmentAuth();
        builder.Services.AddDatabaseFeatures();
        builder.Services.AddOnboarding();
        builder.Services.AddBillingFeatures();
        builder.Services.AddHomeFeatures();
        builder.Services.AddAnalyticsFeatures();
        builder.Services.AddUsageFeatures();
        builder.Services.AddInternalBillingCommands();
        builder.Services.AddSettingsFeatures();
        builder.Services.AddQuerySubscriptions();
        builder.Services.AddOrganizationFeatures();
        builder.Services.AddSingleton<AccessTokens>();
        builder.Services.AddSingleton<IProfileTokens>(services => services.GetRequiredService<AccessTokens>());
        builder.Services.AddDashboardMcp();
        builder.Services.AddHttpClient("platform", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(TerraBaseHttp.CreateHandler);
        configureTestHost?.Invoke(builder);
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            // Fly forces HTTPS at the edge. Infer deployment from its vendor-owned identity,
            // never from a browser-supplied forwarding header or a cookie-security switch.
            if (Environment.GetEnvironmentVariable("FLY_APP_NAME") is not null) context.Request.Scheme = "https";
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            context.Items["nonce"] = nonce;
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            var path = context.Request.Path.Value ?? "";
            var editorStyles = path.StartsWith("/api/v1/dashboard/data/", StringComparison.Ordinal) ||
                path.StartsWith("/api/v1/dashboard/indexes", StringComparison.Ordinal) ||
                path.StartsWith("/databases/", StringComparison.Ordinal) &&
                (path.Contains("/documents", StringComparison.Ordinal) || path.EndsWith("/sql", StringComparison.Ordinal) || path.EndsWith("/schema", StringComparison.Ordinal));
            var styles = editorStyles ? $"style-src 'self' 'nonce-{nonce}'; style-src-attr 'unsafe-inline'; worker-src 'self'" : "style-src 'self'";
            context.Response.Headers["Content-Security-Policy"] =
                $"default-src 'none'; script-src 'self' 'nonce-{nonce}'; {styles}; " +
                "img-src 'self' data:; font-src 'self'; connect-src 'self'; form-action 'self' https://checkout.stripe.com https://billing.stripe.com; base-uri 'none'; frame-ancestors 'none'";
            if (!context.Request.Path.StartsWithSegments("/js") &&
                !context.Request.Path.StartsWithSegments("/css") &&
                !context.Request.Path.StartsWithSegments("/fonts") &&
                !context.Request.Path.StartsWithSegments("/brand") &&
                !context.Request.Path.StartsWithSegments("/flags") &&
                !context.Request.Path.StartsWithSegments("/img"))
                context.Response.Headers.CacheControl = "no-store";
            try
            {
                await next(context);
            }
            catch (AccountAccessException error)
            {
                if (context.Response.HasStarted) throw;
                context.Response.StatusCode = error.StatusCode;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(error.Message + "\n", context.RequestAborted);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or TimeoutException or Stripe.StripeException ||
                                          error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
            {
                // Platform failures never select a demo store or leak response bodies/secrets.
                app.Logger.LogWarning("Dashboard dependency failed ({ErrorType})", error.GetType().Name);
                if (context.Response.HasStarted) throw;
                var view = UI.Slices.ServiceUnavailable.Create(new DocumentModel("Service unavailable", nonce));
                view.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ((IResult)view).ExecuteAsync(context);
            }
        });
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
        app.MapGet("/readyz", async (IDashboardState state, CancellationToken cancellationToken) =>
        {
            await state.CheckReadyAsync(cancellationToken);
            return Results.Text("ok");
        });
        // The original probe URLs: ts-status reads the deployed commit from /health/ready's version.
        app.MapGet("/health/live", () => Results.Json(new { status = "ok", version = BuildVersion }));
        app.MapGet("/health/ready", async (IDashboardState state, CancellationToken cancellationToken) =>
        {
            await state.CheckReadyAsync(cancellationToken);
            return Results.Json(new { status = "ok", version = BuildVersion });
        });
        app.MapAuth();
        app.MapAccountFeatures();
        app.MapOnboarding();
        app.MapDatabaseFeatures();
        app.MapAuthenticationManagement();
        app.MapAdmin();
        app.MapHome();
        app.MapAnalytics();
        app.MapUsage();
        app.MapSettings();
        app.MapQuerySubscriptions();
        app.MapProfile();
        app.MapProfileAccessTokens();
        app.MapNotifications();
        app.MapOrganizations();
        app.MapBilling();
        app.MapBillingPlanChanges();
        app.MapDashboardMcp();
        app.MapDevelopmentUpdates();
        app.MapFallback((HttpContext context) =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            if (context.Request.Method != "GET" || context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Headers.Accept.Any(value => value is not null && (value.Contains("application/json") || value.Contains("text/event-stream"))) ||
                context.Request.Headers.ContainsKey("Datastar-Request"))
                return (IResult)Results.NotFound();
            var view = Features.NotFound.NotFound.Create(new DocumentModel("Page not found", DocumentModel.NonceFor(context)));
            view.StatusCode = StatusCodes.Status404NotFound;
            return view;
        });
        return app;
    }
}
