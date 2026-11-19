using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed record DatabasePositionsAnswer(string Region, string? Primary, string? Replica);

internal sealed class DatabasePositionsEndpoint(TerraBaseOptions options, IDatabaseRevisions revisions, IDatabaseReplicas replicas) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/positions");
        Name("getDatabasePositions");
        Summary("Reads the positions of this node's primary file and replica of a database");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Answers the revision of this node's primary file "
            + "and of its pulled replica of a database, each null when this node holds none, so a home lease repair can prove its primary covers them. "
            + "409 position_unknown when a replica's position cannot be read.");
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
            NoReplica => await AnswerAsync(database, null, cancellationToken).ConfigureAwait(false),
            HeldAt(var held) => await AnswerAsync(database, held, cancellationToken).ConfigureAwait(false),
            PositionUnknown => ErrorResponse.From(new Conflict("the position of this node's replica of the database cannot be read", "position_unknown")),
        };
    }

    private async Task<IResult> AnswerAsync(TenantContext database, DatabaseRevision? replica, CancellationToken cancellationToken) =>
        TypedResults.Json(new DatabasePositionsAnswer(options.Region,
            (await revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false))?.ToString(), replica?.ToString()),
            ReplicasJsonContext.Default.DatabasePositionsAnswer);
}
