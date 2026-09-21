namespace TerraScale.Auth.Webhooks;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Endpoints;

internal sealed class ListWebhooksEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/webhooks", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, CancellationToken ct, AuthStore store, InstanceResolver resolver)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var endpoints = await store.ListWebhookEndpointsAsync(context.Instance.Id, ct);
        return Results.Ok(endpoints.Select(ToListItem).ToArray());
    }

    private static object ToListItem(WebhookEndpoint endpoint) =>
        new { endpoint.Id, endpoint.Url, endpoint.Events, endpoint.Enabled, created_at = endpoint.CreatedAt };
}
