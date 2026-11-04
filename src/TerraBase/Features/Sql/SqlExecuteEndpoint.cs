using Microsoft.AspNetCore.Authorization;
using JsonResult = Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<TerraBase.Features.Sql.SqlErrorBody>;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Sql;

/// <summary><c>POST /v1/projects/{project}/databases/{database}/sql/execute</c>: runs relational SQL statements.</summary>
internal sealed class SqlExecuteEndpoint(
    ISqlService sql, IAuthorizationService authorization, ITenantDirectory tenants, IReplicaCatalog placements, IDatabaseReplicas replicas, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{

    /// <inheritdoc />
    public override void Configure()
    {
        Post(DatabaseRoute.Template + "/sql/execute");
        Name("executeSql");
        Summary("Runs relational SQL statements");
        Description("""
            {"statements":[{"sql":…,"args":[…],"named":{…}}]} with 1 to 256 statements of at most 16 KiB each, in a body of at most 1 MiB. Statements use the SQLite dialect over the database's own tables: CREATE, ALTER and DROP TABLE, indexes (unique, partial, expression), views and triggers pass through, foreign keys are always enforced, and JSON columns are TEXT read with json_extract, -> and ->>. Arguments are null, numbers, booleans (1 or 0), strings, or blobs written {"base64":"…"} (standard base64). Results: one per statement, {"columns","rows","rows_affected","last_insert_rowid"}; row values are null, numbers, strings, or blobs as {"base64":"…"}. All statements run as one transaction: all or nothing; BEGIN, COMMIT and ROLLBACK are refused, SAVEPOINT, RELEASE and ROLLBACK TO are accepted. Internal tables (names starting with _tb_, _ts_ or turso_) do not exist for SQL and are left out of sqlite_schema and PRAGMA table_list. ATTACH, DETACH, VACUUM (INTO included), ANALYZE, temporary and virtual tables, load_extension, file functions, sqlite_dbpage and PRAGMAs other than the schema-reading ones are refused with 403 sql_sandbox. The same statements run over the SQL WebSocket and the Postgres wire with the same results and errors. A refused statement rolls everything back and answers {"error":{"code","message","statement","sqlstate"}} with a TerraScale code and its PostgreSQL SQLSTATE: 400 for SQL errors (syntax_error, undefined_table, undefined_column, duplicate_object, primary_key_violation, unique_violation, not_null_violation, check_violation, foreign_key_violation, constraint_violation, query_canceled, read_only_transaction, transaction_state, invalid_request, sql_error), 403 forbidden_scope (writes and DDL need sql:write) or sql_sandbox, 409 serialization_conflict (retry the request), 413 too_large or 429 quota_exceeded (the plan's monthly write cap or SQL read fair use is used up; shape reads stay available).
            """);
        Tags("Queries");
        Metadata(new RequestBodyMetadata(typeof(SqlExecuteRequest), DatabaseRoute.JsonMediaType));
        Produces<SqlExecuteBody>();
        Produces<SqlErrorBody>(StatusCodes.Status400BadRequest);
        Produces<SqlErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<SqlErrorBody>(StatusCodes.Status409Conflict);
        Produces<SqlErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<SqlErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.SqlRead);
        Metadata(ListenerMetadata.Client, new DatabaseRegions.ReplicaReads());
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DatabaseRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => DatabaseRoute.CheckParameters(HttpContext) switch
            {
                Validation error => ErrorResponse.From(error),
                _ => await DatabaseRoute.CheckBodyAsync(HttpContext, SqlBatch.MaxBytes, "invalid_request", cancellationToken, DatabaseRoute.JsonMediaType) switch
                {
                    Validation error => ErrorResponse.From(error),
                    PayloadTooLarge error => ErrorResponse.From(error),
                    UnsupportedMediaType error => ErrorResponse.From(error),
                    byte[] body => RequestJson.Read<SqlExecuteRequest>(body, "invalid_request") switch
                    {
                        Validation error => ErrorResponse.From(error),
                        SqlExecuteRequest request => await ExecuteAsync(database, request.Statements ?? [], cancellationToken),
                    },
                },
            },
        };

    private async ValueTask<IResult> ExecuteAsync(TenantContext database, IReadOnlyList<SqlExecuteStatement> statements, CancellationToken cancellationToken)
        => SqlBatch.Commands(statements) switch
        {
            SqlBatchFailure invalid => Error(invalid),
            List<SqlCommand> commands => await RunAsync(database, commands, cancellationToken),
        };

    private async ValueTask<IResult> RunAsync(TenantContext database, List<SqlCommand> commands, CancellationToken cancellationToken)
    {
        // Only a node holding a replica of the database reads its placement here; the home answers without another catalog read.
        if (replicas.Holds(database)
            && await placements.FindAsync(database, cancellationToken) is TenantDatabaseRecord { Placement: var placement } && placement.Home != options.Region)
        {
            return await ReadReplicaAsync(database, placement, commands, cancellationToken);
        }

        var canWrite = (await authorization.AuthorizeAsync(HttpContext.User, ApiKeyScopes.SqlWrite)).Succeeded;
        var session = sql.Open(database, canWrite);
        await using (session.ConfigureAwait(false))
        {
            session.BeginRequest();
            var outcome = await SqlBatch.RunAsync(session, commands, cancellationToken);
            await session.EndRequestAsync();
            return outcome switch
            {
                SqlExecuteBody body => TypedResults.Json(body, TerraBaseJsonContext.Default.SqlExecuteBody),
                SqlBatchFailure failure => Error(failure),
            };
        }
    }

    // A replica region answers reads from its replica (docs/terrabase/region-replicas.md); writes, and reads before the replica's
    // first pull, belong to the home.
    private async ValueTask<IResult> ReadReplicaAsync(
        TenantContext database, DatabasePlacement placement, List<SqlCommand> commands, CancellationToken cancellationToken)
    {
        var statements = new List<(SqlStatementPlan, SqlCommand)>(commands.Count);
        for (var i = 0; i < commands.Count; i++)
        {
            switch (SqlScript.Classify(commands[i].Sql))
            {
                case SqlError error:
                    return Error(new SqlBatchFailure(error, i));
                case SqlStatementPlan { Kind: SqlStatementKind.Read } plan:
                    statements.Add((plan, commands[i]));
                    break;
                case SqlStatementPlan { Kind: SqlStatementKind.Write }:
                    return DatabaseRegions.HomeRegion(placement.Home, [placement.Home]);
                case SqlStatementPlan { IsControl: true }:
                    return Error(new SqlBatchFailure(new SqlError(SqlErrors.InvalidRequest, "transaction statements are not accepted: every request runs as one transaction"), i));
                case SqlStatementPlan:
                    break;
            }
        }

        var reader = await replicas.BeginReadAsync(database, cancellationToken);
        if (reader is null)
        {
            return DatabaseRegions.HomeRegion(placement.Home, [placement.Home]);
        }

        await using (reader.ConfigureAwait(false))
        {
            return await sql.ExecuteOnReplicaAsync(database, reader, statements, cancellationToken) switch
            {
                IReadOnlyList<SqlResult> results => TypedResults.Json(new SqlExecuteBody([.. results.Select(result => new SqlStatementBody(
                    [.. result.Columns.Select(column => new SqlColumnBody(column.Name, column.DeclType))], result.Rows, result.RowsAffected, result.LastInsertRowid))]),
                    TerraBaseJsonContext.Default.SqlExecuteBody),
                ReplicaSqlFailure failed => Error(new SqlBatchFailure(failed.Error, failed.Statement)),
            };
        }
    }

    private JsonResult Error(SqlBatchFailure failure)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Json(SqlBatch.Body(failure), TerraBaseJsonContext.Default.SqlErrorBody, statusCode: failure.Error.Status);
    }
}
