using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Platform;

internal sealed class ListPlatformDatabasesEndpoint(CatalogStore catalog, TerraBaseOptions options, TimeProvider time)
    : EndpointWithoutRequest<Result<DatabasesBody, Forbidden, Unavailable>>
{
    public override void Configure()
    {
        Get("/v1/platform/databases");
        Name("listPlatformDatabases");
        Summary("Lists the platform's own databases (ts-auth, ts-dashboard)");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<Result<DatabasesBody, Forbidden, Unavailable>> HandleAsync(CancellationToken cancellationToken)
    {
        if (!PlanFlow.IsOperator(HttpContext.Caller, "platform.read"))
        {
            return new Forbidden("listing platform databases needs the platform.read grant", "forbidden_caller");
        }

        List<DatabaseBody> items = [];
        foreach (var platform in PlatformCatalog.Databases)
        {
            var found = await catalog.GetDatabaseAsync(platform.Id, cancellationToken) switch
            {
                DatabaseRecord record => DatabaseBody.From(record, options.Region, time.GetUtcNow()),
                NotFound or Unavailable => null,
            };
            if (found is not null)
            {
                items.Add(found);
            }
        }

        return new DatabasesBody(items, items, null);
    }
}
