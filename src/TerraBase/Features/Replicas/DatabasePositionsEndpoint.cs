using PositionsJson = Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<TerraBase.Features.Replicas.DatabasePositionsAnswer>;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed record DatabaseCopyPosition(string Revision, bool UserTables);

internal sealed record DatabasePositionsAnswer(string Region, DatabaseCopyPosition? Primary, DatabaseCopyPosition? Replica);

internal sealed class DatabasePositionsEndpoint(TerraBaseOptions options, IStorageEngine engine, IDatabaseRevisions revisions, IDatabaseReplicas replicas)
    : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/positions");
        Name("getDatabasePositions");
        Summary("Reads the positions of this node's primary file and replica of a database");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Answers the revision of this node's primary file "
            + "and of its pulled replica of a database, and whether each holds a user table, each null when this node holds none, so a home lease "
            + "repair can prove its primary file covers them. 409 position_unknown when a replica's position cannot be read.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<DatabasePositionsAnswer>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        if (ReplicaRoutes.Database(HttpContext) is not { } database)
        {
            return ErrorResponse.From(new NotFound("the database does not exist"));
        }

        return replicas.Holding(database) switch
        {
            NoReplica => Answer(await PrimaryAsync(database, cancellationToken).ConfigureAwait(false), null),
            HeldAt(var held) => Answer(await PrimaryAsync(database, cancellationToken).ConfigureAwait(false),
                await ReplicaAsync(database, held, cancellationToken).ConfigureAwait(false)),
            PositionUnknown => ErrorResponse.From(new Conflict("the position of this node's replica of the database cannot be read", "position_unknown")),
        };
    }

    private PositionsJson Answer(DatabaseCopyPosition? primary, DatabaseCopyPosition? replica) =>
        TypedResults.Json(new DatabasePositionsAnswer(options.Region, primary, replica), ReplicasJsonContext.Default.DatabasePositionsAnswer);

    private async Task<DatabaseCopyPosition?> PrimaryAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store
            || await revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false) is not { } revision)
        {
            return null;
        }

        await using var transaction = await store.BeginInspectionTransactionAsync(cancellationToken).ConfigureAwait(false);
        var schema = await transaction.ExecuteStatementAsync(CopySchema.Tables, cancellationToken).ConfigureAwait(false);
        return new DatabaseCopyPosition(revision.ToString(), CopySchema.HoldsUserTables(schema));
    }

    private async Task<DatabaseCopyPosition> ReplicaAsync(TenantContext database, DatabaseRevision held, CancellationToken cancellationToken)
    {
        await using var reader = await replicas.BeginReadAsync(database, cancellationToken).ConfigureAwait(false);
        return new DatabaseCopyPosition(held.ToString(),
            reader is null || CopySchema.HoldsUserTables(await reader.ExecuteAsync(CopySchema.Tables, cancellationToken).ConfigureAwait(false)));
    }
}
