namespace TerraScale.Auth;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
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
        builder.WebHost.UseUrls(options.Listen);
        builder.Services.AddSingleton(options);
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
        builder.Services.AddSingleton<AuthStore>(provider => new AuthStore(provider.GetRequiredService<TerraScaleClient>(), options.Database));
        builder.Services.AddSingleton<KeyRing>();
        builder.Services.AddSingleton<InstanceResolver>();
        builder.Services.AddSingleton<Users.SessionService>();
        builder.Services.AddSingleton<Email.IEmailSender, Email.LoggingEmailSender>();
        return (builder, options);
    }

    /// <summary>Maps all endpoints. Call once on the built application.</summary>
    public static void Configure(WebApplication app, AuthOptions options)
    {
        // First in the pipeline: a storage-engine outage or an unexpected handler fault
        // must answer with the standard error envelope (503/500) — not escape as an
        // unhandled exception, which is a bare body-less 500 through Kestrel. Covers
        // every module and the rate-limit middleware's own instance resolution.
        app.Use(ExceptionEnvelope);

        // Every sk_-authenticated management request is rate limited here, uniformly
        // across all endpoint modules.
        Management.ManagementRateLimit.Use(app);

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
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
