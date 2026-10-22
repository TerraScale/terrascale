using Microsoft.AspNetCore.Authorization;
using JsonResult = Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<TerraBase.Features.Sql.SqlErrorBody>;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Documents;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Sql;

/// <summary><c>POST /v1/projects/{project}/databases/{database}/sql/execute</c>: runs relational SQL statements.</summary>
internal sealed class SqlExecuteEndpoint(ISqlService sql, IAuthorizationService authorization, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    private const int MaxBodyBytes = 1024 * 1024;
    private const int MaxStatements = 1000;

    /// <inheritdoc />
    public override void Configure()
    {
        Post(DocumentRoute.DatabaseRoute + "/sql/execute");
        Name("executeSql");
        Summary("Runs relational SQL statements");
        Description("""
            {"statements":[{"sql":…,"args":[…],"named":{…}}]} with 1 to 1000 statements of at most 16 KiB each, in a body of at most 1 MiB. Tables are collections created with CREATE TABLE; rows are their documents. Arguments are null, numbers, booleans (1 or 0) or strings. Results: one per statement, {"columns","rows","rows_affected","last_insert_rowid"}. A refused statement answers {"error":{"code","message","statement","sqlite_code"}}: 400 sql_error, 403 forbidden_scope (SQLITE_AUTH: writes and DDL need documents:write), 409 sql_conflict (SQLITE_BUSY) or 413 sql_too_large (SQLITE_TOOBIG).
            """);
        Tags("Queries");
        Metadata(new RequestBodyMetadata(typeof(SqlExecuteRequest), DocumentRoute.JsonMediaType));
        Produces<SqlExecuteBody>();
        Produces<SqlErrorBody>(StatusCodes.Status400BadRequest);
        Produces<SqlErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<SqlErrorBody>(StatusCodes.Status409Conflict);
        Produces<SqlErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => DocumentRoute.CheckParameters(HttpContext) switch
            {
                Validation error => ErrorResponse.From(error),
                _ => await DocumentRoute.CheckBodyAsync(HttpContext, MaxBodyBytes, "invalid_request", cancellationToken, DocumentRoute.JsonMediaType) switch
                {
                    Validation error => ErrorResponse.From(error),
                    PayloadTooLarge error => ErrorResponse.From(error),
                    UnsupportedMediaType error => ErrorResponse.From(error),
                    byte[] body => RequestJson.Read<SqlExecuteRequest>(body, "invalid_request") switch
                    {
                        Validation error => ErrorResponse.From(error),
                        SqlExecuteRequest { Statements: { Count: > 0 and <= MaxStatements } statements } => await ExecuteAsync(database, statements, cancellationToken),
                        SqlExecuteRequest => ErrorResponse.From(new Validation("statements holds 1 to 1000 statements")),
                    },
                },
            },
        };

    private async ValueTask<IResult> ExecuteAsync(TenantContext database, IReadOnlyList<SqlExecuteStatement> statements, CancellationToken cancellationToken)
    {
        var commands = new List<SqlCommand>(statements.Count);
        for (var i = 0; i < statements.Count; i++)
        {
            switch (SqlJson.Command(statements[i]))
            {
                case Validation error:
                    return ErrorResponse.From(error, i);
                case SqlCommand command:
                    commands.Add(command);
                    break;
            }
        }

        var canWrite = (await authorization.AuthorizeAsync(HttpContext.User, ApiKeyScopes.DocumentsWrite)).Succeeded;
        var session = sql.Open(database, canWrite);
        await using (session.ConfigureAwait(false))
        {
            var results = new List<SqlStatementBody>(commands.Count);
            for (var i = 0; i < commands.Count; i++)
            {
                switch (await session.ExecuteAsync(commands[i], cancellationToken))
                {
                    case SqlError error:
                        return Error(error, i);
                    case SqlResult result:
                        results.Add(new SqlStatementBody(
                            [.. result.Columns.Select(column => new SqlColumnBody(column.Name, column.DeclType))], result.Rows, result.RowsAffected, result.LastInsertRowid));
                        break;
                }
            }

            await session.EndRequestAsync();
            return TypedResults.Json(new SqlExecuteBody(results), TerraBaseJsonContext.Default.SqlExecuteBody);
        }
    }

    private JsonResult Error(SqlError error, int statement)
    {
        var (status, code) = error.Code switch
        {
            "SQLITE_AUTH" => (StatusCodes.Status403Forbidden, "forbidden_scope"),
            "SQLITE_BUSY" => (StatusCodes.Status409Conflict, "sql_conflict"),
            "SQLITE_TOOBIG" => (StatusCodes.Status413PayloadTooLarge, "sql_too_large"),
            _ => (StatusCodes.Status400BadRequest, "sql_error"),
        };
        HttpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Json(new SqlErrorBody(new SqlErrorDetail(code, error.Message, statement, error.Code)), TerraBaseJsonContext.Default.SqlErrorBody, statusCode: status);
    }
}
