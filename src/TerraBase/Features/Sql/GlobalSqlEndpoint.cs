using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Sql;

internal sealed class GlobalSqlEndpoint(SqlFlow flow)
    : Endpoint<SqlRequestInput, ExecuteSqlResponse>
{
    public override void Configure()
    {
        Post("/v1/sql");
        Name("runScopedSql");
        Summary("Runs SQL on the database a database-scoped credential names");
        Description("For API keys and project tokens narrowed to one database: the database is taken from the credential instead of the path.");
        Tags("Queries");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client);
    }

    public override async ValueTask<ExecuteSqlResponse> HandleAsync(
        SqlRequestInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var unscoped = new InvalidInput("/v1/sql needs a credential narrowed to one database; otherwise use /v1/projects/{project}/databases/{database}/sql",
            "database_required");
        return HttpContext.Caller switch
        {
            ApiKeyCaller { Key.DatabaseId: { } databaseId } => SqlRoute.V2(await flow.ExecuteByIdAsync(HttpContext, databaseId, request.Statements, cancellationToken)),
            ProjectCaller { Database: { } database } token => SqlRoute.V2(await flow.ExecuteAsync(HttpContext, token.ProjectId.ToString(), database, request.Statements, cancellationToken)),
            EndUserCaller { Database: { } database } endUser => SqlRoute.V2(await flow.ExecuteAsync(HttpContext, endUser.ProjectId.ToString(), database, request.Statements, cancellationToken)),
            AnonymousCaller => new CredentialRejected("SQL needs a bearer credential", "unauthorized"),
            PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => unscoped,
        };
    }
}
