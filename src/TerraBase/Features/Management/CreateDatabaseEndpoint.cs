using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class CreateDatabaseEndpoint(DatabaseAccess access, ManagementService management, TerraBaseOptions options, TimeProvider time)
    : Endpoint<DatabaseInput, CreateDatabaseResponse>
{
    public override void Configure()
    {
        Post(ManagementRoute.Databases);
        Name("createDatabase");
        Summary("Creates a database");
        Description("Needs databases:write. regions (default: the answering region) are equal active-active peers; every region accepts writes. "
            + "defaultConsistency is fast (default) or strong; strict makes every write strong; ttlSeconds (at least 60) deletes it when it expires.");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<CreateDatabaseResponse> HandleAsync(DatabaseInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await access.ProjectAsync(HttpContext.Caller, request.Project, Scopes.DatabasesWrite, cancellationToken);
        return outcome switch
        {
            ResolvedProject project => await management.CreateDatabaseAsync(project.Project, request, cancellationToken) switch
            {
                DatabaseRecord created => new Created<DatabaseBody>(
                    string.Concat("/v1/projects/", created.ProjectId.ToString(), "/databases/", created.Name), DatabaseBody.From(created, options.Region, time.GetUtcNow())),
                InvalidInput invalid => invalid,
                CallerForbidden forbidden => forbidden,
                StateConflict conflict => conflict,
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
