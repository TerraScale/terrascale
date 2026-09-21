namespace TerraScale.Auth;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Observability;
using TerraScale.Auth.Store;
using TerraScale.DotNet;
using TerraScale.Endpoints;

/// <summary>Builds and wires the ts-auth application.</summary>
public static class App
{
    /// <summary>Creates the application builder with all services registered.</summary>
    public static (WebApplicationBuilder Builder, AuthOptions Options) CreateBuilder(string[] args)
    {
        var options = AuthOptions.Load(args);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        if (!builder.Environment.IsDevelopment() && options.AuditSigningKey is null)
            throw new InvalidOperationException("TS_AUTH_AUDIT_SIGNING_KEY is required outside Development; audit authenticity cannot be disabled in production.");
        if (!builder.Environment.IsDevelopment() && options.MetricsKey is null)
            throw new InvalidOperationException("TS_AUTH_METRICS_KEY is required outside Development; the private metrics surface must remain scrapeable and separately authenticated.");
        builder.WebHost.UseUrls(options.Listen);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<AuthMetrics>();
        builder.Services.AddSingleton(_ => new TerraScaleClient(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = options.TerrascaleApiUrl },
            new TerraScaleClientOptions
            {
                BaseAddress = options.TerrascaleApiUrl,
                ApiKey = options.TerrascaleApiKey,
                ProjectId = options.TerrascaleProjectId,
                TenantId = options.TerrascaleTenantId,
                MaxRetryAttempts = 2,
            }));
        builder.Services.AddSingleton<AuthStore>(provider => new AuthStore(
            provider.GetRequiredService<TerraScaleClient>(), options.Database, options.AuditSigningKey, options.AuditVerificationKeys));
        builder.Services.AddSingleton<KeyRing>();
        builder.Services.AddSingleton<InstanceResolver>();
        builder.Services.AddSingleton<Users.SessionService>();
        builder.Services.AddSingleton<Webhooks.WebhookDispatcher>(sp => new Webhooks.WebhookDispatcher(
            sp.GetRequiredService<AuthStore>(),
            sp.GetRequiredService<ILogger<Webhooks.WebhookDispatcher>>(),
            options.MasterKey));
        if (options.SmtpHost is { } smtpHost)
            builder.Services.AddSingleton<Email.IEmailSender>(sp => new Email.SmtpEmailSender(
                smtpHost, options.SmtpPort, options.SmtpUsername!, options.SmtpPassword!, options.SmtpFromAddress!,
                sp.GetRequiredService<ILogger<Email.SmtpEmailSender>>()));
        else
            builder.Services.AddSingleton<Email.IEmailSender, Email.LoggingEmailSender>();
        builder.Services.AddTerraScaleEndpoints(EndpointModules.All, endpointOptions =>
        {
            endpointOptions.Title = "TerraScale Auth API";
            endpointOptions.Version = "v1";
        });
        return (builder, options);
    }

    /// <summary>Maps middleware, OpenAPI, and all feature endpoints.</summary>
    public static void Configure(WebApplication app, AuthOptions options)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/metrics", StringComparison.Ordinal))
            {
                await next();
                return;
            }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { await next(); }
            finally
            {
                context.RequestServices.GetRequiredService<AuthMetrics>().RecordRequest(
                    context.Request.Method, context.Response.StatusCode, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }
        });
        app.Use(ExceptionEnvelope);
        Management.ManagementGate.Use(app);
        EndpointModules.MapAll(app);
        Webhooks.WebhooksModule.Start(app);
        app.MapTerraScaleOpenApi();
    }

    private static async Task ExceptionEnvelope(HttpContext context, Func<Task> next)
    {
        try { await next(); }
        catch (Exception) when (context.Response.HasStarted) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            var unavailable = exception is HttpRequestException
                || (exception is TerraScaleApiException api && api.StatusCode >= System.Net.HttpStatusCode.InternalServerError);
            context.Response.StatusCode = unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = new { code = unavailable ? "temporarily_unavailable" : "internal_error", message = unavailable ? "The storage engine is unavailable; retry shortly." : "An unexpected error occurred." } });
        }
    }
}
