using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union ListDatabasesResponse(DatabasesBody, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal sealed class ListDatabasesEndpoint(DatabaseAccess access, CatalogStore catalog, TerraBaseOptions options, TimeProvider time)
    : Endpoint<PageInput, ListDatabasesResponse>
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

    public override async ValueTask<ListDatabasesResponse> HandleAsync(
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
                    BackendUnavailable unavailable => unavailable,
                },
                InvalidInput invalid => invalid,
                CredentialRejected unauthorized => unauthorized,
                CallerForbidden forbidden => forbidden,
                ResourceNotFound missing => missing,
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
        };
    }

    private DatabasesBody Body(CatalogPage<DatabaseRecord> page)
    {
        IReadOnlyList<DatabaseBody> items = [.. page.Items.Select(database => DatabaseBody.From(database, options.Region, time.GetUtcNow()))];
        return new DatabasesBody(items, items, page.Next);
    }
}
