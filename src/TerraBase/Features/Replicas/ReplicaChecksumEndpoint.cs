using System.Globalization;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class ReplicaChecksumEndpoint(ITenantDirectory tenants, IDatabaseRevisions revisions) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/checksums");
        Name("getReplicaChecksums");
        Summary("Reads one round of a database's range checksums at a revision");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Anti-entropy for region replicas: the schema hash and "
            + "rowid-range hashes of every table at revision, continuing after table and after from the previous round's next. moved is true, with no ranges, "
            + "when the database is no longer at revision.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<ReplicaChecksumPage>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var query = HttpContext.Request.Query;
        if (!DatabaseRevision.TryParse(query["revision"].ToString(), out var revision))
        {
            return ErrorResponse.From(new Validation("revision must be a database revision"));
        }

        ChecksumCursor? cursor = null;
        if (query["table"] is [{ } table])
        {
            if (table.Length > 1024 || !long.TryParse(query["after"].ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var after))
            {
                return ErrorResponse.From(new Validation("table and after must come from an earlier round's next"));
            }

            cursor = new ChecksumCursor(table, after);
        }

        return await tenants.FindDatabaseAsync(Value("organization"), Value("project"), Value("database"), cancellationToken).ConfigureAwait(false) switch
        {
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await RoundAsync(database, revision, cursor, cancellationToken).ConfigureAwait(false),
        };
    }

    private async ValueTask<IResult> RoundAsync(TenantContext database, DatabaseRevision revision, ChecksumCursor? cursor, CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return await revisions.ChecksumAsync(database, revision, cursor, cancellationToken).ConfigureAwait(false) switch
        {
            null => ErrorResponse.From(new NotFound("the database is not stored on this node")),
            var round => TypedResults.Json(new ReplicaChecksumPage(round.Revision.ToString(), round.Ranges is null, round.Ranges ?? [], round.Next),
                TerraBaseJsonContext.Default.ReplicaChecksumPage),
        };
    }

    private string Value(string name) => HttpContext.Request.RouteValues[name] as string ?? string.Empty;
}
