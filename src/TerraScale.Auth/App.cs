namespace TerraScale.Auth;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Observability;
using TerraScale.DotNet;

/// <summary>Builds and wires the ts-auth application. Split so tests can swap
/// the TerraScale client for the embedded engine before the app is built.</summary>
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
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                BaseAddress = options.TerrascaleApiUrl,
            },
            new TerraScaleClientOptions
            {
                BaseAddress = options.TerrascaleApiUrl,
                ApiKey = options.TerrascaleApiKey,
                ProjectId = options.TerrascaleProjectId,
                TenantId = options.TerrascaleTenantId,
                MaxRetryAttempts = 2,
            }));
        builder.Services.AddSingleton<AuthStore>(provider => new AuthStore(
            provider.GetRequiredService<TerraScaleClient>(),
            options.Database,
            options.AuditSigningKey,
            options.AuditVerificationKeys));
        builder.Services.AddSingleton<KeyRing>();
        builder.Services.AddSingleton<InstanceResolver>();
        builder.Services.AddSingleton<Users.SessionService>();
        if (options.SmtpHost is { } smtpHost)
            builder.Services.AddSingleton<Email.IEmailSender>(sp => new Email.SmtpEmailSender(
                smtpHost, options.SmtpPort, options.SmtpUsername!, options.SmtpPassword!, options.SmtpFromAddress!,
                sp.GetRequiredService<ILogger<Email.SmtpEmailSender>>()));
        else
            builder.Services.AddSingleton<Email.IEmailSender, Email.LoggingEmailSender>();
        return (builder, options);
    }

    /// <summary>Maps all endpoints. Call once on the built application.</summary>
    public static void Configure(WebApplication app, AuthOptions options)
    {
        // Record the final status after the exception envelope has translated storage
        // failures into 503 responses. Keeping this outermost prevents failed requests
        // from being counted as 200s by a finally block inside the envelope.
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
                    context.Request.Method,
                    context.Response.StatusCode,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }
        });

        // A storage-engine outage or an unexpected handler fault
        // must answer with the standard error envelope (503/500) — not escape as an
        // unhandled exception, which is a bare body-less 500 through Kestrel. Covers
        // every module and the rate-limit middleware's own instance resolution.
        app.Use(ExceptionEnvelope);

        // Every sk_-authenticated management request is rate limited here, uniformly
        // across all endpoint modules.
        Management.ManagementGate.Use(app);

        ObservabilityEndpoints.Map(app);
        EndpointModules.MapAll(app);

        // Operator onboarding: create an instance and its first API key.
        app.MapPost("/v1/instances", async (HttpRequest request, AuthStore store, CancellationToken ct) =>
        {
            var authorization = request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(authorization["Bearer ".Length..]),
                    System.Text.Encoding.UTF8.GetBytes(options.OperatorKey)))
                return ApiResults.Unauthorized();

            var body = await request.ReadFromJsonAsync<CreateInstanceRequest>(ct);
            if (body is null || string.IsNullOrWhiteSpace(body.Slug) || string.IsNullOrWhiteSpace(body.Name))
                return ApiResults.BadRequest("invalid_request", "slug and name are required");

            var slug = body.Slug.Trim().ToLowerInvariant();
            if (slug.Length is < 3 or > 48 || !slug.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return ApiResults.BadRequest("invalid_request", "slug must be 3-48 url-safe characters");
            if (await store.FindInstanceBySlugAsync(slug, ct) is not null)
                return ApiResults.Conflict("instance_exists", "an instance with this slug already exists");

            var (keyId, secret, combined) = Crypto.SecureTokens.NewSecret("sk");
            var instance = new Instance
            {
                Id = "ins_" + Crypto.SecureTokens.RandomToken(20),
                Slug = slug,
                Name = body.Name.Trim(),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            await store.CreateInstanceAsync(instance, ct);
            await store.CreateApiKeyAsync(new ApiKey
            {
                Id = keyId,
                InstanceId = instance.Id,
                Name = "default",
                // Hash the full presented key ("sk_id_secret"); InstanceResolver hashes the whole bearer.
                SecretKey = Crypto.SecureTokens.Sha256Hex(combined),
                Scopes = ["*"],
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct);

            return Results.Ok(new { instance = new { instance.Id, instance.Slug, instance.Name }, api_key = combined });
        });
    }

    /// <summary>Terminal exception filter: engine failures answer 503, anything else 500 — both as the uniform error envelope.</summary>
    private static async Task ExceptionEnvelope(HttpContext context, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (Exception exception) when (exception is OperationCanceledException || context.Response.HasStarted)
        {
            throw;
        }
        catch (Exception exception)
        {
            var unavailable = exception is HttpRequestException
                || (exception is TerraScaleApiException api && api.StatusCode >= System.Net.HttpStatusCode.InternalServerError);
            context.Response.StatusCode = unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = unavailable ? "temporarily_unavailable" : "internal_error",
                    message = unavailable ? "The storage engine is unavailable; retry shortly." : "An unexpected error occurred.",
                },
            });
        }
    }
}

/// <summary>Operator request body for instance creation.</summary>
public sealed record CreateInstanceRequest(string Slug, string Name);
