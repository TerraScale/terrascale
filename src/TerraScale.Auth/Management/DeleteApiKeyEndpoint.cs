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

internal sealed class DeleteApiKeyEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/v1/{slug}/api-keys/{keyId}", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, string keyId, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        var gate = await RequireInstanceAsync(request, slug, resolver, ct);
        if (gate.Failure is { } failure)
            return failure;
        var context = gate.Context!;

        var key = await store.GetApiKeyAsync(keyId, ct);
        if (key is null || key.InstanceId != context.Instance.Id)
            return ApiResults.NotFound("api_key_not_found", "no api key with this id in this instance");

        await store.UpsertApiKeyAsync(key with { Revoked = true }, ct);
        await AuditAsync(store, context, "api_key.revoked", new() { ["api_key_id"] = key.Id }, ct);
        return Results.NoContent();
    }
}
