using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Webhooks;

/// <summary>Lists collection webhooks without signing secrets.</summary>
internal sealed class ListWebhooksEndpoint(IManagementService management, IWebhooks webhooks) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(WebhookRoute.Collection);
        Name("listCollectionWebhooks");
        Summary("Lists collection change webhooks");
        Description(WebhookRoute.Description + " Direct array in ascending ULID order. Query after=<ULID>, limit=1..100 (default 50). Status is active or feed_gap.");
        Tags("Webhooks");
        Produces<WebhookBody[]>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => WebhookRoute.Page(HttpContext) switch
    {
        Validation error => ValueTask.FromResult(ErrorResponse.From(error)),
        PageQuery page => WebhookRoute.RunAsync(HttpContext, management,
            (tenant, collection) => webhooks.ListAsync(tenant, collection, page.After, page.Limit, cancellationToken), cancellationToken),
    };
}
