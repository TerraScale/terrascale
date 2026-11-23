using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class SetDatabaseTtlEndpoint(DatabaseAccess access, ManagementService management, TerraBaseOptions options, TimeProvider time)
    : Endpoint<DatabaseInput, UpdateDatabaseResponse>
{
    public override void Configure()
    {
        Put(ManagementRoute.Database + "/ttl");
        Name("setDatabaseTtl");
        Summary("Sets when the database expires");
        Description("{\"ttlSeconds\": n} with n at least 60; the database and its archived history are deleted when it expires.");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<UpdateDatabaseResponse> HandleAsync(DatabaseInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.TtlSeconds is null
            ? new InvalidInput("ttlSeconds is required", "invalid_ttl")
            : await DatabaseUpdates.TtlAsync(HttpContext.Caller, access, management, options, time, request, request.TtlSeconds, cancellationToken);
    }
}
