using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Webhooks;

/// <summary>Lists a webhook's bounded, sanitized delivery history.</summary>
internal sealed class ListWebhookDeliveriesEndpoint(IManagementService management, IWebhooks webhooks) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(WebhookRoute.Item + "/deliveries");
        Name("listCollectionWebhookDeliveries");
        Summary("Lists webhook delivery history");
        Description(WebhookRoute.Description + " Latest 100 delivery records retained; direct array in ascending ULID order. Query after=<ULID>, limit=1..100 (default 50). No signing secrets, payloads, receiver bodies or exception text.");
        Tags("Webhooks");
        Produces<WebhookDeliveryBody[]>();
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
            (tenant, collection) => webhooks.DeliveriesAsync(tenant, collection, HttpContext.Request.RouteValues["webhook"]!.ToString()!, page.After, page.Limit, cancellationToken), cancellationToken),
    };
}
