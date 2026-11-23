using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union GetDatabaseResponse(DatabaseBody, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal sealed class GetDatabaseEndpoint(DatabaseAccess access, TerraBaseOptions options, TimeProvider time)
    : Endpoint<DatabaseInput, GetDatabaseResponse>
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

    public override async ValueTask<GetDatabaseResponse> HandleAsync(
        DatabaseInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await access.ManagedAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => DatabaseBody.From(resolved.Database, options.Region, time.GetUtcNow()),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
