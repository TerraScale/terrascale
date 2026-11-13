using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed record RetentionStatus(bool Retained);

internal sealed class RetentionStatusEndpoint(IStorageEngine engine, RegionalOwners owners) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/retention");
        Name("getDatabaseRetention");
        Summary("Confirms whether a missing database is restorable");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<RetentionStatus>();
        Produces(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!owners.Writer || ReplicaRoutes.Database(HttpContext) is not { } database)
        {
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var retained = await SystemCatalog.GetAsync(transaction, DeletedDatabases.Kind, database.DatabaseId!,
            ManagementJsonContext.Default.DeletedDatabaseRecord, cancellationToken).ConfigureAwait(false);
        if (!owners.Writer)
        {
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        return TypedResults.Json(new RetentionStatus(retained is not null && DatabasePurge.SameDatabase(retained.Storage, database)),
            ReplicasJsonContext.Default.RetentionStatus);
    }
}
