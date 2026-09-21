namespace TerraScale.Auth.Webhooks;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Endpoints;

internal sealed class ListDeadLettersEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/webhooks/dead-letter", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, CancellationToken ct, AuthStore store, InstanceResolver resolver)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();
        var deliveries = await store.ListWebhookDeliveriesAsync(context.Instance.Id, ct);
        return Results.Ok(deliveries.Where(d => d.Status == "dead").OrderByDescending(d => d.UpdatedAt).Select(d => new
        {
            d.Id,
            endpoint_id = d.EndpointId,
            event_id = d.EventId,
            d.Attempts,
            last_error = d.LastError,
            updated_at = d.UpdatedAt,
        }).ToArray());
    }
}
