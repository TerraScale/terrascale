using TerraBase.Hosting;
using TerraScale.Endpoints;
using TerraScale.Contracts.Http;
using BoundSqlRequest = TerraScale.Endpoints.Binding.Bound<TerraBase.Features.Sql.SqlExecuteInput>;

namespace TerraBase.Features.Sql;

internal sealed class SqlEndpoint(SqlFlow flow)
    : Endpoint<SqlExecuteInput, Result<ExecuteBody, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Post(SqlRoute.Database + "/sql");
        Name("runSql");
        Summary("Runs SQL statements with the v2 wire format");
        Description("Same semantics as sql/execute; the body answers {results, bookmark, consistency} in the TerraScale v2 wire format.");
        Tags("Queries");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client, SqlFlow.RequestSizeLimit);
        Produces<ApiErrorEnvelope>(StatusCodes.Status413PayloadTooLarge);
    }

    protected override ValueTask<BoundSqlRequest> BindAsync(CancellationToken cancellationToken) =>
        SqlFlow.BindAsync(HttpContext.Request, base.BindAsync, cancellationToken);

    public override async ValueTask<Result<ExecuteBody, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        SqlExecuteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ReferenceEquals(request, SqlFlow.OversizedRequest))
        {
            return SqlRefusal.RequestTooLarge();
        }

        return SqlRoute.V2(await flow.ExecuteAsync(HttpContext, request.Project, request.Database, request.Statements, cancellationToken));
    }
}
