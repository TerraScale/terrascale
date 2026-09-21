namespace TerraScale.Auth.Instances;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Observability;
using TerraScale.DotNet;
using TerraScale.Endpoints;

internal sealed class CreateInstanceEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/instances", HandleAsync);

    private static async Task<IResult> HandleAsync(HttpRequest request, AuthStore store, CancellationToken ct, AuthOptions options)
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
    }
}
