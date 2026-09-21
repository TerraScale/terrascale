namespace TerraScale.Auth.Management;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Auth.Api;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;
using TerraScale.Endpoints;
using static TerraScale.Auth.Management.ManagementSupport;

internal sealed class CreateApiKeyEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/api-keys", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        var gate = await RequireInstanceAsync(request, slug, resolver, ct);
        if (gate.Failure is { } failure)
            return failure;
        var context = gate.Context!;

        var (body, bodyFailure) = await ReadBodyAsync<CreateApiKeyBody>(request, ct);
        if (body is null)
            return bodyFailure ?? ApiResults.BadRequest("invalid_request", "a JSON body is required");
        if (string.IsNullOrWhiteSpace(body.Name))
            return ApiResults.BadRequest("invalid_request", "name is required");

        // The bearer credential is the full "sk_id_secret" string; only its sha256 is stored,
        // matching what InstanceResolver looks up.
        var (id, _, combined) = SecureTokens.NewSecret("sk");
        var key = new ApiKey
        {
            Id = id,
            InstanceId = context.Instance.Id,
            Name = body.Name.Trim(),
            SecretKey = SecureTokens.Sha256Hex(combined),
            Scopes = body.Scopes is { Length: > 0 } ? body.Scopes : ["*"],
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await store.CreateApiKeyAsync(key, ct);
        await AuditAsync(store, context, "api_key.created", new() { ["api_key_id"] = key.Id }, ct);
        return Results.Json(new { id = key.Id, name = key.Name, api_key = combined, scopes = key.Scopes }, statusCode: 201);
    }

    /// <summary>Scopes are Management API route families ("users", "webhooks", ...),
    /// enforced by <see cref="ManagementGate"/>; omitted scopes mean "*" (full access).</summary>
    private sealed record CreateApiKeyBody(string? Name, string[]? Scopes);
}
