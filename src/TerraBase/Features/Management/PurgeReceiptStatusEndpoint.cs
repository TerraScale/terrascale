using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class PurgeReceiptStatusEndpoint(EngineManagementService management) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/" + RemoteDatabases.PurgeReceiptsPath + "/{database}");
        Name("purgeReceiptStatus");
        Summary("Reports whether this node's database erasure receipt delivery to its current regional writer is settled");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<PurgeReceiptStatus>();
        Produces(StatusCodes.Status400BadRequest);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var id = HttpContext.Request.RouteValues["database"]?.ToString();
        if (!EntityIds.IsValid(id))
        {
            return TypedResults.BadRequest();
        }

        var status = await management.PurgeReceiptStatusAsync(id, cancellationToken).ConfigureAwait(false);
        HttpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Json(status, ManagementJsonContext.Default.PurgeReceiptStatus);
    }
}
