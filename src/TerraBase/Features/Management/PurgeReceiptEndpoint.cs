using System.Diagnostics;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class PurgeReceiptEndpoint(EngineManagementService management) : Endpoint<DatabasePurgeReceipt, IResult>
{
    public override void Configure()
    {
        Put("/" + RemoteDatabases.PurgeReceiptsPath);
        Name("recordPeerPurgeReceipt");
        Summary("Records the erasure receipt of a purge holder that is not its region's writer");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces(StatusCodes.Status204NoContent);
        Produces(StatusCodes.Status404NotFound);
        Produces(StatusCodes.Status409Conflict);
        Produces(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(DatabasePurgeReceipt request, CancellationToken cancellationToken) =>
        await management.RecordPeerReceiptAsync(request, cancellationToken).ConfigureAwait(false) switch
        {
            PeerReceiptAnswer.Recorded => TypedResults.NoContent(),
            PeerReceiptAnswer.Unknown => TypedResults.NotFound(),
            PeerReceiptAnswer.Unbound => TypedResults.Conflict(),
            PeerReceiptAnswer.NotWriter => TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable),
            _ => throw new UnreachableException(),
        };
}
