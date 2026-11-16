using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class PurgeReceiptEndpoint(EngineManagementService management) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/" + RemoteDatabases.PurgeReceiptsPath + "/{database}/receipt");
        Name("heldPurgeReceipt");
        Summary("Returns this node's erasure receipt for a purged database, for its region's writer to record");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<DatabasePurgeReceipt>();
        Produces(StatusCodes.Status400BadRequest);
        Produces(StatusCodes.Status404NotFound);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var id = HttpContext.Request.RouteValues["database"]?.ToString();
        if (!EntityIds.IsValid(id))
        {
            return TypedResults.BadRequest();
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        return await management.HeldReceiptAsync(id, cancellationToken).ConfigureAwait(false) is { } receipt
            ? TypedResults.Json(receipt, ManagementJsonContext.Default.DatabasePurgeReceipt)
            : TypedResults.NotFound();
    }
}
