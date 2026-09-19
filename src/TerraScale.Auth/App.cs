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

    /// <summary>Maps endpoints and the schema-bootstrap hook. Call once on the built application.</summary>
    public static void Configure(WebApplication app, AuthOptions options)
    {
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

        app.Lifetime.ApplicationStarted.Register(() => _ = BootstrapSchemaAsync(app, options));

        static async Task BootstrapSchemaAsync(WebApplication app, AuthOptions options)
        {
            try
            {
                var store = app.Services.GetRequiredService<AuthStore>();
                var client = app.Services.GetRequiredService<TerraScaleClient>();
                await SchemaBootstrap.EnsureSchemaAsync(client, options.Database);
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("startup")
                    .LogInformation("ts-auth schema ready on database {Database}; listening on {Listen}", options.Database, options.Listen);
            }
            catch (Exception exception)
            {
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("startup")
                    .LogError(exception, "schema bootstrap failed for database {Database}", options.Database);
            }
        }
    }
}

/// <summary>Operator request body for instance creation.</summary>
public sealed record CreateInstanceRequest(string Slug, string Name);
