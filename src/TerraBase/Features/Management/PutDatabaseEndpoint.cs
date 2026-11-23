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
        return await access.ProjectAsync(HttpContext.Caller, request.Project, Scopes.DatabasesWrite, cancellationToken) switch
        {
            ResolvedProject when request.Database is null => new Validation("database names the database", "invalid_database"),
            ResolvedProject project => await catalog.FindDatabaseAsync(project.Project.Id, request.Database, cancellationToken) switch
            {
                DatabaseRecord existing => DatabaseBody.From(existing, options.Region, time.GetUtcNow()),
                NotFound => await management.CreateDatabaseAsync(project.Project, request with { Name = request.Database }, cancellationToken) switch
                {
                    DatabaseRecord created => new Created<DatabaseBody>(
                        string.Concat("/v1/projects/", created.ProjectId.ToString(), "/databases/", created.Name), DatabaseBody.From(created, options.Region, time.GetUtcNow())),
                    Validation invalid => invalid,
                    Forbidden forbidden => forbidden,
                    Conflict conflict => conflict,
                    QuotaExceeded quota => quota,
                    Unavailable unavailable => unavailable,
                },
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
