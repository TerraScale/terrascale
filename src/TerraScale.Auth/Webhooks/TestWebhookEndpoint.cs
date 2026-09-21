namespace TerraScale.Auth.Webhooks;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Endpoints;

internal sealed class TestWebhookEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/webhooks/{id}/test", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string id, HttpRequest request, CancellationToken ct, AuthStore store, InstanceResolver resolver, WebhookDispatcher dispatcher)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        if (await store.GetWebhookEndpointAsync(id, ct) is not { } endpoint || endpoint.InstanceId != context.Instance.Id)
            return ApiResults.NotFound();

        await store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + Crypto.SecureTokens.RandomToken(20),
            InstanceId = context.Instance.Id,
            Type = "webhook.test",
            ActorId = context.ApiKeyId,
            Data = new Dictionary<string, object> { ["endpoint_id"] = id },
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        await dispatcher.EnsureInstanceAsync(context.Instance.Id, ct);
        return Results.Ok(new { queued = true });
    }
}
