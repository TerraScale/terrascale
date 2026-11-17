using TerraBase.Errors;
using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed record HomeLeaseRepairBody(string Outcome, string Node, long Epoch, string? PreviousNode, long? PreviousEpoch);

internal sealed class RepairHomeLeaseEndpoint(HomeLeaseRepair repair) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Metadata(new Requires("admin"));
        Post("/admin/organizations/{organization}/projects/{project}/databases/{database}/home-lease");
        Name("repairHomeLease");
        Summary("Re-stamps a stale home lease to this home writer at the placement version");
        Description("Internal peer listener only, behind the peer credential or an admin caller key; 404 elsewhere. Version 1. Call it on the writer of the "
            + "database's home region. It changes only the home lease, never table data, and refuses with 409 and a code unless every regional writer "
            + "records the same home and placement version, the node the lease names answers that it is not its region's writer or records an older "
            + "placement, and this node's applied-position proof passes. A lease this node already holds at the placement version answers unchanged.");
        Tags("Administration");
        Produces<HomeLeaseRepairBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (ReplicaRoutes.Database(HttpContext) is not { } database)
        {
            return ErrorResponse.From(new NotFound("the database does not exist"));
        }

        var caller = HttpContext.Features.Get<CallerIdentity>()?.Key.Kid ?? "the peer credential";
        return await repair.RepairAsync(database, caller, cancellationToken).ConfigureAwait(false) switch
        {
            LeaseRestamped(var previous, var lease) => TypedResults.Json(
                new HomeLeaseRepairBody("restamped", lease.Node, lease.Epoch, previous?.Node, previous?.Epoch), ReplicasJsonContext.Default.HomeLeaseRepairBody),
            LeaseUnchanged(var lease) => TypedResults.Json(
                new HomeLeaseRepairBody("unchanged", lease.Node, lease.Epoch, lease.Node, lease.Epoch), ReplicasJsonContext.Default.HomeLeaseRepairBody),
            LeaseRepairRefused(var code, var reason) => ErrorResponse.From(new Conflict(reason, code)),
        };
    }
}
