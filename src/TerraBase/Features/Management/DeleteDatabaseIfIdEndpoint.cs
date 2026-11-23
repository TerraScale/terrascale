using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class DeleteDatabaseIfIdEndpoint(DatabaseAccess access, ManagementService management) : Endpoint<DatabaseInput, DeleteDatabaseResponse>
{
    public override void Configure()
    {
        Delete(ManagementRoute.Database + "/if-id/{expectedId}");
        Name("deleteDatabaseIfId");
        Summary("Deletes the database only when its id is the expected one");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<DeleteDatabaseResponse> HandleAsync(DatabaseInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await DatabaseDeletion.DeleteAsync(HttpContext.Caller, access, management, request, cancellationToken);
    }
}
