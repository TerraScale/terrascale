using TerraBase.Hosting;
using TerraScale.Endpoints;
using TerraScale.Contracts.Http;
using BoundSqlRequest = TerraScale.Endpoints.Binding.Bound<TerraBase.Features.Sql.SqlExecuteInput>;

namespace TerraBase.Features.Sql;

internal readonly union LegacyExecuteSqlResponse(LegacyExecuteBody, SqlRefusal, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal sealed class SqlExecuteEndpoint(SqlFlow flow)
    : Endpoint<SqlExecuteInput, LegacyExecuteSqlResponse>
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

    public override async ValueTask<LegacyExecuteSqlResponse> HandleAsync(
        SqlExecuteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ReferenceEquals(request, SqlFlow.OversizedRequest))
        {
            return SqlRefusal.RequestTooLarge();
        }

        if (request.Durability is not (null or "local" or "all"))
        {
            return new InvalidInput("durability is local or all", "invalid_request");
        }

        var outcome = await flow.ExecuteAsync(HttpContext, request, cancellationToken);
        return outcome switch
        {
            SqlRun run => SqlWire.Legacy(run.Results),
            SqlRefusal refusal => refusal,
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
