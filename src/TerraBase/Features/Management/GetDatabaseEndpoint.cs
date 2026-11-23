using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class GetDatabaseEndpoint(DatabaseAccess access, TerraBaseOptions options, TimeProvider time)
    : Endpoint<DatabaseInput, Result<DatabaseBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get(ManagementRoute.Database);
        Name("getDatabase");
        Summary("Reads a database by ULID or name");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<DatabaseBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        DatabaseInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await access.ManagedAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken) switch
        {
            ResolvedDatabase resolved => DatabaseBody.From(resolved.Database, options.Region, time.GetUtcNow()),
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
