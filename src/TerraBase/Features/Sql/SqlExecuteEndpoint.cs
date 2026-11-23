using TerraBase.Hosting;
using TerraScale.Endpoints;
using TerraScale.Contracts.Http;
using BoundSqlRequest = TerraScale.Endpoints.Binding.Bound<TerraBase.Features.Sql.SqlExecuteInput>;

namespace TerraBase.Features.Sql;

internal sealed class SqlExecuteEndpoint(SqlFlow flow)
    : Endpoint<SqlExecuteInput, Result<LegacyExecuteBody, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Post(SqlRoute.Database + "/sql/execute");
        Name("executeSql");
        Summary("Runs 1 to 256 SQL statements as one atomic transaction");
        Description("{\"statements\":[{\"sql\",\"args\",\"named\"}]} runs as one transaction in this region. Send TerraScale-Bookmark to read your writes "
            + "from any region and TerraScale-Consistency: fast (default) or strong; BEGIN STRONG or BEGIN ISOLATION LEVEL SERIALIZABLE ... COMMIT around the "
            + "batch runs it as a strong 2-of-3 quorum transaction. Every answer carries TerraScale-Bookmark and TerraScale-Consistency.");
        Tags("Queries");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client, SqlFlow.RequestSizeLimit);
        Produces<ApiErrorEnvelope>(StatusCodes.Status413PayloadTooLarge);
    }

    protected override ValueTask<BoundSqlRequest> BindAsync(CancellationToken cancellationToken) =>
        SqlFlow.BindAsync(HttpContext.Request, base.BindAsync, cancellationToken);

    public override async ValueTask<Result<LegacyExecuteBody, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        SqlExecuteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ReferenceEquals(request, SqlFlow.OversizedRequest))
        {
            return SqlRefusal.RequestTooLarge();
        }

        if (request.Durability is not (null or "local" or "all"))
        {
            return new Validation("durability is local or all", "invalid_request");
        }

        return await flow.ExecuteAsync(HttpContext, request.Project, request.Database, request.Statements, cancellationToken) switch
        {
            SqlRun run => SqlWire.Legacy(run.Results),
            SqlRefusal refusal => refusal,
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
