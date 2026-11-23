using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class BranchDatabaseIfIdEndpoint(BranchFlow flow) : Endpoint<DatabaseInput, BranchDatabaseResponse>
{
    public override void Configure()
    {
        Post(ManagementRoute.Database + "/branches/if-id/{expectedId}");
        Name("branchDatabaseIfId");
        Summary("Branches the database only when its id is the expected one");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<BranchDatabaseResponse> HandleAsync(DatabaseInput request, CancellationToken cancellationToken) =>
        await flow.BranchAsync(HttpContext.Caller, request, cancellationToken);
}
