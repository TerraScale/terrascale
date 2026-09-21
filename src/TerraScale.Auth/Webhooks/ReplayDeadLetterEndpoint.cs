namespace TerraScale.Auth.Webhooks;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Endpoints;

internal sealed class ReplayDeadLetterEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/webhooks/dead-letter/{id}/replay", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string id, HttpRequest request, CancellationToken ct, InstanceResolver resolver, WebhookDispatcher dispatcher)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();
        var replayed = await dispatcher.ReplayAsync(context.Instance.Id, id, ct);
        return replayed ? Results.Ok(new { replayed = true }) : ApiResults.NotFound();
    }
}
