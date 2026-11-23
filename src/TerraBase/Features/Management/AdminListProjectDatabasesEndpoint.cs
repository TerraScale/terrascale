using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class AdminListProjectDatabasesEndpoint(CatalogStore catalog, TerraBaseOptions options, TimeProvider time)
    : Endpoint<AdminInput, Result<DatabasesBody, Validation, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get("/admin/projects/{project}/databases");
        Name("adminListProjectDatabases");
        Summary("Lists any project's databases");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<Result<DatabasesBody, Validation, Forbidden, NotFound, Unavailable>> HandleAsync(AdminInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "catalog.read"))
        {
            return new Forbidden("listing databases needs the catalog.read grant", "forbidden_caller");
        }

        if (ManagementService.Id(request.Project) is not { } projectId)
        {
            return new NotFound("the project does not exist", "unknown_project");
        }

        return ManagementService.Limit(request.Limit) switch
        {
            int limit => await catalog.ListDatabasesAsync(projectId, request.After, limit, cancellationToken) switch
            {
                CatalogPage<DatabaseRecord> page => Body(page),
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
