using Microsoft.AspNetCore.Authorization;
using JsonResult = Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<TerraBase.Features.Sql.SqlErrorBody>;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.HomeApply;
using TerraBase.Features.HomePush;
using TerraBase.Features.Management;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Features.Sql.Routing;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Sql;

/// <summary><c>POST /v1/projects/{project}/databases/{database}/sql/execute</c>: runs relational SQL statements.</summary>
internal sealed class SqlExecuteEndpoint(
    ISqlService sql, IAuthorizationService authorization, ITenantDirectory tenants, IReplicaCatalog placements, IDatabaseReplicas replicas, IReplicaPeers peers,
    WriteRouter router, ILocalWrites local, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{

    /// <inheritdoc />
    public override void Configure()
    {
        Post(DatabaseRoute.Template + "/sql/execute");
        Name("executeSql");
        Summary("Runs relational SQL statements");
        Description("""
            {"statements":[{"sql":…,"args":[…],"named":{…}}]} with 1 to 256 statements of at most 16 KiB each, in a body of at most 1 MiB. Statements use the SQLite dialect over the database's own tables: CREATE, ALTER and DROP TABLE, indexes (unique, partial, expression), views and triggers pass through, foreign keys are always enforced, and JSON columns are TEXT read with json_extract, -> and ->>. Arguments are null, numbers, booleans (1 or 0), strings, or blobs written {"base64":"…"} (standard base64). Results: one per statement, {"columns","rows","rows_affected","last_insert_rowid"}; row values are null, numbers, strings, or blobs as {"base64":"…"}. All statements run as one transaction: all or nothing. In a region that holds a replica of the database, read-only requests run on the replica and every other request is forwarded whole to the database's home in one hop; the answer carries TerraScale-Write-Route (route=local|home; reason=…; commit=home|local) and Server-Timing. Send TerraScale-Write: home to run a request at the home (read your writes), local to prefer the region, or auto; send TerraScale-Durability: home to let the region commit a write it routes local and answer only after the home applied it (a write the home rejects answers its constraint error and is applied nowhere), or local (the default: the region commits it once its second node holds it too, and forwards it home when the region has one healthy node); BEGIN, COMMIT and ROLLBACK are refused, SAVEPOINT, RELEASE and ROLLBACK TO are accepted. Internal tables (names starting with _tb_, _ts_ or turso_) do not exist for SQL and are left out of sqlite_schema and PRAGMA table_list. ATTACH, DETACH, VACUUM (INTO included), ANALYZE, temporary and virtual tables, load_extension, file functions, sqlite_dbpage and PRAGMAs other than the schema-reading ones are refused with 403 sql_sandbox. The same statements run over the SQL WebSocket and the Postgres wire with the same results and errors. A refused statement rolls everything back and answers {"error":{"code","message","statement","sqlstate"}} with a TerraScale code and its PostgreSQL SQLSTATE: 400 for SQL errors (syntax_error, undefined_table, undefined_column, duplicate_object, primary_key_violation, unique_violation, not_null_violation, check_violation, foreign_key_violation, constraint_violation, query_canceled, read_only_transaction, transaction_state, invalid_request, sql_error), 403 forbidden_scope (writes and DDL need sql:write) or sql_sandbox, 409 serialization_conflict (retry the request), 413 too_large or 429 quota_exceeded (the plan's monthly write cap or SQL read fair use is used up; shape reads stay available).
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
                        SqlExecuteRequest request => await ExecuteAsync(database, request.Statements ?? [], body, cancellationToken),
                    },
                },
            },
        };

    internal const string WriteHeader = "TerraScale-Write";
    internal const string RouteHeader = "TerraScale-Write-Route";
    internal const string DurabilityHeader = "TerraScale-Durability";

    internal static bool? HomeDurable(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "local" => false,
        "home" => true,
        _ => null,
    };

    internal static WriteOverride? Preference(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => WriteOverride.Auto,
        "home" => WriteOverride.Home,
        "local" => WriteOverride.Local,
        _ => null,
    };

    private async ValueTask<IResult> ExecuteAsync(TenantContext database, IReadOnlyList<SqlExecuteStatement> statements, byte[] body, CancellationToken cancellationToken)
        => SqlBatch.Commands(statements) switch
        {
            SqlBatchFailure invalid => Error(invalid),
            List<SqlCommand> commands => await RunAsync(database, commands, body, cancellationToken),
        };

    private async ValueTask<IResult> RunAsync(TenantContext database, List<SqlCommand> commands, byte[] body, CancellationToken cancellationToken)
    {
        // Only a node holding a replica of the database reads its placement here; the home answers without another catalog read.
        if (replicas.Holds(database)
            && await placements.FindAsync(database, cancellationToken) is TenantDatabaseRecord { Placement: var placement } && placement.Home != options.Region)
        {
            return await ReplicaAsync(database, placement, commands, body, cancellationToken);
        }

        if (await SettledAsync(database, cancellationToken) is { } unsettled)
        {
            return unsettled;
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
                SqlExecuteBody answered => TypedResults.Json(answered, TerraBaseJsonContext.Default.SqlExecuteBody),
                SqlBatchFailure failure => Error(failure),
            };
        }
    }

    // A replica region answers reads from its replica (docs/terrabase/region-replicas.md); writes, and reads before the replica's
    // first pull, belong to the home.
    private async ValueTask<IResult> ReplicaAsync(
        TenantContext database, DatabasePlacement placement, List<SqlCommand> commands, byte[] body, CancellationToken cancellationToken)
    {
        if (Preference(HttpContext.Request.Headers[WriteHeader]) is not { } preference)
        {
            return Error(new SqlBatchFailure(new SqlError(SqlErrors.InvalidRequest, "TerraScale-Write is home, local or auto"), 0));
        }

        if (HomeDurable(HttpContext.Request.Headers[DurabilityHeader]) is not { } homeDurable)
        {
            return Error(new SqlBatchFailure(new SqlError(SqlErrors.InvalidRequest, "TerraScale-Durability is home or local"), 0));
        }

        var statements = new List<(SqlStatementPlan, SqlCommand)>(commands.Count);
        var every = new List<(SqlStatementPlan, SqlCommand)>(commands.Count);
        var writes = false;
        for (var i = 0; i < commands.Count; i++)
        {
            switch (SqlScript.Classify(commands[i].Sql))
            {
                case SqlError error:
                    return Error(new SqlBatchFailure(error, i));
                case SqlStatementPlan { Kind: SqlStatementKind.Read } plan:
                    statements.Add((plan, commands[i]));
                    every.Add((plan, commands[i]));
                    break;
                case SqlStatementPlan { Kind: SqlStatementKind.Write } plan:
                    writes = true;
                    every.Add((plan, commands[i]));
                    break;
                case SqlStatementPlan { IsControl: true }:
                    return Error(new SqlBatchFailure(new SqlError(SqlErrors.InvalidRequest, "transaction statements are not accepted: every request runs as one transaction"), i));
                case SqlStatementPlan:
                    break;
            }
        }

        IReplicaReader? reader;
        try
        {
            reader = await replicas.BeginReadAsync(database, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            reader = null;
        }
        if (reader is null || writes || preference == WriteOverride.Home)
        {
            WriteRoute? route = null;
            if (writes)
            {
                route = await router.RouteAsync(database, reader, [.. commands.Select(command => command.Sql)], preference, homeDurable, cancellationToken);
            }

            if (reader is not null)
            {
                await reader.DisposeAsync();
            }

            if (route?.Commit == WritePath.Local && every.Count == commands.Count
                && (await authorization.AuthorizeAsync(HttpContext.User, ApiKeyScopes.SqlWrite)).Succeeded
                && await LocalAsync(database, route, every, homeDurable, cancellationToken) is { } answered)
            {
                return answered;
            }

            var forwarded = await ForwardAsync(placement.Home, route is { Commit: WritePath.Local } ? route with { Commit = WritePath.Home } : route, body,
                writes ? database : null, cancellationToken);
            if (writes)
            {
                local.Forwarded(database);
            }

            return forwarded;
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

    private async ValueTask<IResult?> SettledAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (!Listeners.IsForwarded(HttpContext) || HttpContext.Request.Headers[HomeForwarding.PushThroughHeader] is not [{ } through])
        {
            return null;
        }

        if (through.Split(' ') is not [var origin, var position] || !TerraBaseOptions.IsRegion(origin)
            || !ulong.TryParse(position, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sequence))
        {
            return Error(new SqlBatchFailure(new SqlError(SqlErrors.InvalidRequest, "the forwarded push position is malformed"), 0));
        }

        return await local.AppliedAsync(origin, database, sequence, cancellationToken)
            ? null
            : Error(new SqlBatchFailure(new SqlError(SqlErrors.Unavailable, "the region's earlier writes have not reached the home yet; retry"), 0));
    }

    private async ValueTask<IResult?> LocalAsync(
        TenantContext database, WriteRoute route, List<(SqlStatementPlan, SqlCommand)> statements, bool homeDurable, CancellationToken cancellationToken)
    {
        ILocalTransaction? transaction;
        try
        {
            transaction = await local.BeginAsync(database, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            transaction = null;
        }

        if (transaction is null)
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            var outcome = await sql.ExecuteOnCopyAsync(database, statement => transaction.ExecuteAsync(statement, cancellationToken), statements, write: true, cancellationToken);
            if (outcome is ReplicaSqlFailure failed)
            {
                return Error(new SqlBatchFailure(failed.Error, failed.Statement));
            }

            if (outcome is not IReadOnlyList<SqlResult> results)
            {
                return null;
            }

            LocalCommit committed;
            try
            {
                committed = await transaction.CommitAsync(homeDurable, cancellationToken);
            }
            catch (LocalWriteRefusedException)
            {
                return null;
            }
            catch (LocalOutcomeUnknownException)
            {
                return Error(new SqlBatchFailure(new SqlError(SqlErrors.Unavailable, "the write reached the push stream but its outcome is unknown; read it at the home"), 0));
            }

            sql.RecordLocalWrites(database, results.Sum(result => result.RowsAffected));
            HttpContext.Response.Headers.CacheControl = "no-store";
            HttpContext.Response.Headers[RouteHeader] = route.Header;
            if (committed.Home is { State: WriteStates.Reverted } reverted)
            {
                return Error(new SqlBatchFailure(Reverted(reverted), 0));
            }

            return TypedResults.Json(new SqlExecuteBody([.. results.Select(result => new SqlStatementBody(
                [.. result.Columns.Select(column => new SqlColumnBody(column.Name, column.DeclType))], result.Rows, result.RowsAffected, result.LastInsertRowid))]),
                TerraBaseJsonContext.Default.SqlExecuteBody);
        }
    }

    private static SqlError Reverted(HomeApplyResult result)
    {
        var conflict = result.Conflicts.Count > 0 ? result.Conflicts[0] : null;
        var code = conflict?.Kind switch
        {
            "unique" => SqlErrors.UniqueViolation,
            "primary_key" => SqlErrors.PrimaryKeyViolation,
            "not_null" => SqlErrors.NotNullViolation,
            "check" => SqlErrors.CheckViolation,
            "foreign_key" => SqlErrors.ForeignKeyViolation,
            "constraint" => SqlErrors.ConstraintViolation,
            _ => SqlErrors.TransactionRolledBack,
        };
        return new SqlError(code, conflict?.Message ?? "the database's home rejected the write");
    }

    private async ValueTask<IResult> ForwardAsync(string home, WriteRoute? route, byte[] body, TenantContext? writing, CancellationToken cancellationToken)
    {
        if ((await peers.ListAsync(cancellationToken)).FirstOrDefault(peer => peer.Region == home) is not { } node)
        {
            return DatabaseRegions.HomeRegion(home, [home]);
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        if (route is not null)
        {
            HttpContext.Response.Headers[RouteHeader] = route.Header;
        }

        await HomeForwarding.ForwardAsync(HttpContext, new HomeNode(home, node.Internal), body,
            writing is not null && local.LastPushed(writing) is { } pushed ? $"{options.Region} {pushed}" : null);
        return Results.Empty;
    }

    private JsonResult Error(SqlBatchFailure failure)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Json(SqlBatch.Body(failure), TerraBaseJsonContext.Default.SqlErrorBody, statusCode: failure.Error.Status);
    }
}
