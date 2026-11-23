using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class ClearDatabaseTtlEndpoint(DatabaseAccess access, ManagementService management, TerraBaseOptions options, TimeProvider time)
    : Endpoint<DatabaseInput, UpdateDatabaseResponse>
{
    public override void Configure()
    {
        Delete(ManagementRoute.Database + "/ttl");
        Name("clearDatabaseTtl");
        Summary("Keeps the database until it is deleted");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<UpdateDatabaseResponse> HandleAsync(DatabaseInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await DatabaseUpdates.TtlAsync(HttpContext.Caller, access, management, options, time, request, null, cancellationToken);
    }
}
