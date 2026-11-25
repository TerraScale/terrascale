using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class PutDatabaseEndpoint(DatabaseAccess access, CatalogStore catalog, ManagementService management, TerraBaseOptions options, TimeProvider time)
    : Endpoint<DatabaseInput, EnsureDatabaseResponse>
{
    public override void Configure()
    {
        Put(ManagementRoute.Database);
        Name("ensureDatabase");
        Summary("Creates the named database unless it exists");
        Description("Idempotent: answers 200 with the existing database or 201 with a new one named {database}.");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<EnsureDatabaseResponse> HandleAsync(DatabaseInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await access.ProjectAsync(HttpContext.Caller, request.Project, Scopes.DatabasesWrite, cancellationToken);
        return outcome switch
        {
            ResolvedProject when request.Database is null => new InvalidInput("database names the database", "invalid_database"),
            ResolvedProject project => await catalog.FindDatabaseAsync(project.Project.Id, request.Database, cancellationToken) switch
            {
                DatabaseRecord existing when DatabaseAccess.Reaches(HttpContext.Caller, existing) => DatabaseBody.From(existing, options.Region, time.GetUtcNow()),
                DatabaseRecord => DatabaseAccess.UnknownDatabase,
                ResourceNotFound when !DatabaseAccess.MayCreate(HttpContext.Caller, request.Database) => DatabaseAccess.NarrowedCreate,
                ResourceNotFound => await management.CreateDatabaseAsync(project.Project, request with { Name = request.Database }, cancellationToken) switch
                {
                    DatabaseRecord created => new Created<DatabaseBody>(
                        string.Concat("/v1/projects/", created.ProjectId.ToString(), "/databases/", created.Name), DatabaseBody.From(created, options.Region, time.GetUtcNow())),
                    InvalidInput invalid => invalid,
                    CallerForbidden forbidden => forbidden,
                    StateConflict conflict => conflict,
                    BackendUnavailable unavailable => unavailable,
                },
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
