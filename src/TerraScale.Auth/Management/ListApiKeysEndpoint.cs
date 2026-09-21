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

internal sealed class ListApiKeysEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/api-keys", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        var gate = await RequireInstanceAsync(request, slug, resolver, ct);
        if (gate.Failure is { } failure)
            return failure;
        var context = gate.Context!;

        var keys = (await store.ListApiKeysAsync(context.Instance.Id, ct))
            .Select(ApiKeyView)
            .ToList();
        return Results.Json(new { data = keys });
    }

    private static Dictionary<string, object?> ApiKeyView(ApiKey key) => new()
    {
        ["id"] = key.Id,
        ["name"] = key.Name,
        ["scopes"] = key.Scopes,
        ["created_at"] = key.CreatedAt,
        ["last_used_at"] = key.LastUsedAt,
        ["revoked"] = key.Revoked,
    };
}
