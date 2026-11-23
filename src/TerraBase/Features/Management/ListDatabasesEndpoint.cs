using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class ListDatabasesEndpoint(DatabaseAccess access, CatalogStore catalog, TerraBaseOptions options, TimeProvider time)
    : Endpoint<PageInput, Result<DatabasesBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get(ManagementRoute.Databases);
        Name("listDatabases");
        Summary("Lists a project's databases");
        Description("Needs databases:read. Query: after (exclusive cursor), limit (1 to 100, default 20).");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<DatabasesBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        PageInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ManagementService.Limit(request.Limit) switch
        {
            int limit => await access.ProjectAsync(HttpContext.Caller, request.Project, Scopes.DatabasesRead, cancellationToken) switch
            {
                ResolvedProject project => await catalog.ListDatabasesAsync(project.Project.Id, request.After, limit, cancellationToken) switch
                {
                    CatalogPage<DatabaseRecord> page => Body(page),
                    Unavailable unavailable => unavailable,
                },
                Validation invalid => invalid,
                Unauthorized unauthorized => unauthorized,
                Forbidden forbidden => forbidden,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
        };
    }

    private DatabasesBody Body(CatalogPage<DatabaseRecord> page)
    {
        IReadOnlyList<DatabaseBody> items = [.. page.Items.Select(database => DatabaseBody.From(database, options.Region, time.GetUtcNow()))];
        return new DatabasesBody(items, items, page.Next);
    }
}
