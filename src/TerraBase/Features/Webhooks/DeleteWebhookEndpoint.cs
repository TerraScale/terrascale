using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Webhooks;

/// <summary>Deletes a collection webhook and its delivery history.</summary>
internal sealed class DeleteWebhookEndpoint(IManagementService management, IWebhooks webhooks) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(WebhookRoute.Item);
        Metadata(AnchoredRevocation.Instance);
        Name("deleteCollectionWebhook");
        Summary("Deletes a collection change webhook");
        Description(WebhookRoute.Description + " Stops future retries; an already in-flight request may complete. Removes delivery history.");
        Tags("Webhooks");
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        WebhookRoute.RunAsync(HttpContext, management,
            (tenant, collection) => webhooks.DeleteAsync(tenant, collection, HttpContext.Request.RouteValues["webhook"]!.ToString()!, cancellationToken), cancellationToken);
}
