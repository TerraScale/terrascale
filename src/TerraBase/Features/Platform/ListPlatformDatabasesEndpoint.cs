using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Platform;

internal readonly union ListPlatformDatabasesResponse(DatabasesBody, CallerForbidden, BackendUnavailable);

internal sealed class ListPlatformDatabasesEndpoint(CatalogStore catalog, TerraBaseOptions options, TimeProvider time)
    : EndpointWithoutRequest<ListPlatformDatabasesResponse>
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

    public override async ValueTask<ListPlatformDatabasesResponse> HandleAsync(CancellationToken cancellationToken)
    {
        if (!PlanFlow.IsOperator(HttpContext.Caller, "platform.read"))
        {
            return new CallerForbidden("listing platform databases needs the platform.read grant", "forbidden_caller");
        }

        List<DatabaseBody> items = [];
        foreach (var platform in PlatformCatalog.Databases)
        {
            var foundOutcome = await catalog.GetDatabaseAsync(platform.Id, cancellationToken);
            var found = foundOutcome switch
            {
                DatabaseRecord record => DatabaseBody.From(record, options.Region, time.GetUtcNow()),
                ResourceNotFound or BackendUnavailable => null,
            };
            if (found is not null)
            {
                items.Add(found);
            }
        }

        return new DatabasesBody(items, items, null);
    }
}
