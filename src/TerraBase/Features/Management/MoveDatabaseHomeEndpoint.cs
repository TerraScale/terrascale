using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class MoveDatabaseHomeEndpoint(DatabaseAccess access, ManagementService management, TerraBaseOptions options, TimeProvider time)
    : Endpoint<DatabaseInput, UpdateDatabaseResponse>
{
    public override void Configure()
    {
        Post(ManagementRoute.Database + "/move-home");
        Name("moveDatabaseHome");
        Summary("Places the database in a region");
        Description("Regions are equal active-active peers in v2, so there is no home to move; {\"region\"} adds that region to the database's placement.");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<UpdateDatabaseResponse> HandleAsync(DatabaseInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Region is null
            ? new Validation("region is required", "invalid_region")
            : await DatabaseUpdates.UpdateAsync(HttpContext.Caller, access, management, options, time, request with { Regions = null, ReplicaRegions = null }, cancellationToken);
    }
}
