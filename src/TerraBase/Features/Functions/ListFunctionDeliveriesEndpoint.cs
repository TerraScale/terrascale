using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Webhooks;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Functions;

/// <summary>Lists a change-triggered function's deliveries.</summary>
internal sealed class ListFunctionDeliveriesEndpoint(IManagementService management, IFunctions functions) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(FunctionRoute.Item + "/deliveries");
        Name("listFunctionDeliveries");
        Summary("Lists a change-triggered function's deliveries");
        Description(FunctionRoute.Description + " The trigger webhook's delivery history: pending deliveries with attempts and errorCode are the dead-letter view. Empty for a function without a trigger.");
        Tags("Functions");
        Produces<WebhookDeliveryBody[]>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        FunctionRoute.RunAsync(HttpContext, management, (tenant, name) => functions.DeliveriesAsync(tenant, name, cancellationToken), cancellationToken);
}
