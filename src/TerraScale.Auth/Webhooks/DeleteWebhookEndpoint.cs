namespace TerraScale.Auth.Webhooks;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Endpoints;

internal sealed class DeleteWebhookEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/v1/{slug}/webhooks/{id}", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string id, HttpRequest request, CancellationToken ct, AuthStore store, InstanceResolver resolver)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        if (await store.GetWebhookEndpointAsync(id, ct) is not { } endpoint || endpoint.InstanceId != context.Instance.Id)
            return ApiResults.NotFound();
        await store.DeleteWebhookEndpointAsync(id, ct);
        await store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + Crypto.SecureTokens.RandomToken(20),
            InstanceId = context.Instance.Id,
            Type = "webhook.deleted",
            ActorId = context.ApiKeyId,
            Data = new Dictionary<string, object> { ["endpoint_id"] = id },
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        return Results.NoContent();
    }
}
