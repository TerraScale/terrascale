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
    // The transaction log limit: every request is one transaction.
    private const int MaxStatements = 256;

    /// <inheritdoc />
    public override void Configure()
    {
        Post(DocumentRoute.DatabaseRoute + "/sql/execute");
        Name("executeSql");
        Summary("Runs relational SQL statements");
        Description("""
            {"statements":[{"sql":…,"args":[…],"named":{…}}]} with 1 to 256 statements of at most 16 KiB each, in a body of at most 1 MiB. Statements are SQLite SQL over the database's own tables: CREATE, ALTER and DROP TABLE, indexes (unique, partial, expression), views and triggers pass through, foreign keys are always enforced, and JSON columns are TEXT read with json_extract, -> and ->>. Arguments are null, numbers, booleans (1 or 0), strings, or blobs written {"base64":"…"} (standard base64). Results: one per statement, {"columns","rows","rows_affected","last_insert_rowid"}; row values are null, numbers, strings, or blobs as {"base64":"…"}. All statements run as one transaction: all or nothing; BEGIN, COMMIT and ROLLBACK are refused, SAVEPOINT, RELEASE and ROLLBACK TO are accepted. Names starting with _tb_ or turso_, ATTACH, DETACH, VACUUM, ANALYZE and PRAGMAs other than the schema-reading ones are refused. A refused statement rolls everything back and answers {"error":{"code","message","statement","sqlite_code"}}: 400 sql_error (constraint failures carry SQLITE_CONSTRAINT_PRIMARYKEY, _UNIQUE, _NOTNULL, _CHECK or _FOREIGNKEY), 403 forbidden_scope (SQLITE_AUTH: writes and DDL need documents:write), 409 sql_conflict (SQLITE_BUSY) or 413 sql_too_large (SQLITE_TOOBIG).
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
                        SqlExecuteRequest => ErrorResponse.From(new Validation("statements holds 1 to 256 statements")),
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

        var plans = commands.Select(command => SqlScript.Classify(command.Sql)).ToList();
        if (plans.FindIndex(plan => plan is SqlStatementPlan { IsControl: true }) is var control and >= 0)
        {
            return ErrorResponse.From(new Validation("transaction statements are not accepted: every request runs as one transaction"), control);
        }

        var canWrite = (await authorization.AuthorizeAsync(HttpContext.User, ApiKeyScopes.DocumentsWrite)).Succeeded;
        var session = sql.Open(database, canWrite);
        await using (session.ConfigureAwait(false))
        {
            var begin = plans.Exists(plan => plan is SqlStatementPlan { Kind: SqlStatementKind.Write }) ? "BEGIN" : "BEGIN READ ONLY";
            _ = await session.ExecuteAsync(new SqlCommand(begin, [], new Dictionary<string, SqlValue>()), cancellationToken);
            var results = new List<SqlStatementBody>(commands.Count);
            var reads = new List<SqlReadCharge>(commands.Count);
            for (var i = 0; i < commands.Count; i++)
            {
                switch (await session.ExecuteAsync(commands[i], cancellationToken))
                {
                    case SqlError error:
                        _ = await session.ExecuteAsync(new SqlCommand("ROLLBACK", [], new Dictionary<string, SqlValue>()), cancellationToken);
                        return Error(error, i);
                    case SqlResult result:
                        if (result.ReadCharge is { } charge)
                        {
                            reads.Add(charge);
                        }

                        results.Add(new SqlStatementBody(
                            [.. result.Columns.Select(column => new SqlColumnBody(column.Name, column.DeclType))], result.Rows, result.RowsAffected, result.LastInsertRowid));
                        break;
                }
            }

            if (await session.ExecuteAsync(new SqlCommand("COMMIT", [], new Dictionary<string, SqlValue>()), cancellationToken) is SqlError failed)
            {
                return Error(failed, commands.Count - 1);
            }

            await session.EndRequestAsync();
            reads.ForEach(charge => charge.Accept());
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
