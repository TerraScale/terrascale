using JsonResult = Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<TerraBase.Features.Sql.SqlErrorBody>;
using Microsoft.AspNetCore.Authorization;
using System.Globalization;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.HomeApply;
using TerraBase.Features.HomePush;
using TerraBase.Features.Management;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Features.Shapes;
using TerraBase.Features.Sql.Routing;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Sql;

internal sealed class SqlExecuteEndpoint(
    ISqlService sql, IAuthorizationService authorization, ITenantDirectory tenants, IReplicaCatalog placements, IDatabaseReplicas replicas, IReplicaPeers peers,
    WriteRouter router, ILocalWrites local, ReplicaHolds holds, TerraBaseOptions options, ShapeOverlay overlay) : EndpointWithoutRequest<IResult>
{

    public override void Configure()
    {
        Post(DatabaseRoute.Template + "/sql/execute");
        Name("executeSql");
        Summary("Runs relational SQL statements");
        Description("""
            {"statements":[{"sql":…,"args":[…],"named":{…}}]} with 1 to 256 statements of at most 16 KiB each, in a body of at most 1 MiB. Statements use the SQLite dialect over the database's own tables: CREATE, ALTER and DROP TABLE, indexes (unique, partial, expression), views and triggers pass through, foreign keys are always enforced, and JSON columns are TEXT read with json_extract, -> and ->>. Arguments are null, numbers, booleans (1 or 0), strings, or blobs written {"base64":"…"} (standard base64). Results: one per statement, {"columns","rows","rows_affected","last_insert_rowid"}; row values are null, numbers, strings, or blobs as {"base64":"…"}. All statements run as one transaction: all or nothing. In a region that holds a replica of the database, read-only requests run on the replica and every other request is forwarded whole to the database's home in one hop; the answer carries TerraScale-Write-Route (route=local|home; reason=…; commit=home|local) and Server-Timing. Send TerraScale-Write: home to run a request at the home (read your writes), local to prefer the region, or auto; send TerraScale-Durability: home to let the region commit a write it routes local and answer only after the home applied it (a write the home rejects answers its constraint error and is applied nowhere), or local (the default: the region commits on its discovered regional quorum, two copies when a regional partner is present and healthy; a region with no partner, or an unhealthy partner, forwards the write home); BEGIN, COMMIT and ROLLBACK are refused, SAVEPOINT, RELEASE and ROLLBACK TO are accepted. Internal tables (reserved engine prefixes) do not exist for SQL and are left out of sqlite_schema and PRAGMA table_list. ATTACH, DETACH, VACUUM (INTO included), ANALYZE, temporary and virtual tables, load_extension, file functions, sqlite_dbpage and PRAGMAs other than the schema-reading ones are refused with 403 sql_sandbox. The same statements run over the SQL WebSocket and the Postgres wire with the same results and errors. A refused statement rolls everything back and answers {"error":{"code","message","statement","sqlstate"}} with a TerraScale code and its PostgreSQL SQLSTATE: 400 for SQL errors (syntax_error, undefined_table, undefined_column, duplicate_object, primary_key_violation, unique_violation, not_null_violation, check_violation, foreign_key_violation, constraint_violation, query_canceled, read_only_transaction, transaction_state, invalid_request, sql_error), 403 forbidden_scope (writes and DDL need sql:write) or sql_sandbox, 409 serialization_conflict (retry the request), 413 too_large or 429 quota_exceeded (the plan's monthly write cap or SQL read fair use is used up; shape reads stay available).
            Set the body field durability to all to answer only after every replica region holds the committed write, or local (the default). If every replica has not acknowledged within 10 seconds, all answers 503 unavailable after the home commit; its outcome is uncertain if the home then fails.
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

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        DatabaseRoute.TenantAsync(HttpContext, tenants, ServeAsync, cancellationToken);

    private async ValueTask<IResult> ServeAsync(TenantContext database, CancellationToken cancellationToken) =>
        DatabaseRoute.CheckParameters(HttpContext) switch
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
                    SqlExecuteRequest { Durability: not (null or "local" or "all") } => ErrorResponse.From(new Validation("durability is local or all", "invalid_request")),
                    SqlExecuteRequest request => await ExecuteAsync(database, request.Statements ?? [], body, request.Durability == "all", cancellationToken),
                },
            },
        };

    internal const string RouteHeader = "TerraScale-Write-Route";
    internal const string DurabilityHeader = "TerraScale-Durability";

    internal static bool? HomeDurable(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "local" => false,
        "home" => true,
        _ => null,
    };


    private async ValueTask<IResult> ExecuteAsync(TenantContext database, IReadOnlyList<SqlExecuteStatement> statements, byte[] body, bool all,
        CancellationToken cancellationToken)
        => SqlBatch.Commands(statements) switch
        {
            SqlBatchFailure invalid => Error(invalid),
            List<SqlPlannedCommand> commands => await RunAsync(database, commands, body, all, cancellationToken),
        };

    private async ValueTask<IResult> RunAsync(TenantContext database, List<SqlPlannedCommand> commands, byte[] body, bool all, CancellationToken cancellationToken)
    {
        SqlRequestTiming.Mark(HttpContext, "tenant");
        if (await placements.FindAsync(database, cancellationToken) is TenantDatabaseRecord { Placement: var placement } && placement.Home != options.Region)
        {
            return await ReplicaAsync(database, placement, commands, body, all, cancellationToken);
        }

        if (await SettledAsync(database, cancellationToken) is { } unsettled)
        {
            return unsettled;
        }

        var canWrite = (await authorization.AuthorizeAsync(HttpContext.User, ApiKeyScopes.SqlWrite)).Succeeded;
        var session = sql.Open(database, canWrite);
        await using (session.ConfigureAwait(false))
        {
            var forwarded = Listeners.IsForwarded(HttpContext);
            if (forwarded)
            {
                session.KeepCommitted();
            }

            session.BeginRequest();
            var outcome = await SqlBatch.RunAsync(session, commands, cancellationToken);
            await session.EndRequestAsync();
            if (outcome is SqlExecuteBody && all && !await holds.WaitAllAsync(database, cancellationToken))
            {
                return Error(new SqlBatchFailure(new SqlError(SqlErrors.Unavailable,
                    "the write committed at the home but not every replica region held it within 10 s; it may be lost if the home region fails"), commands.Count - 1));
            }

            if (forwarded && outcome is SqlExecuteBody && ShapeOverlay.EncodeCommits(session.Committed) is { } committed)
            {
                HttpContext.Response.Headers[ShapeOverlay.CommitHeader] = committed;
            }

            return outcome switch
            {
                SqlExecuteBody answered => TypedResults.Json(answered, SqlJsonContext.Default.SqlExecuteBody),
                SqlBatchFailure failure => Error(failure),
            };
        }
    }

    private async ValueTask<IResult> ReplicaAsync(
        TenantContext database, DatabasePlacement placement, List<SqlPlannedCommand> commands, byte[] body, bool all, CancellationToken cancellationToken)
    {
        if (WritePreference.Parse(HttpContext.Request.Headers[WritePreference.Header]) is not { } preference)
        {
            return Error(new SqlBatchFailure(new SqlError(SqlErrors.InvalidRequest, "TerraScale-Write is home, local or auto"), 0));
        }

        if (HomeDurable(HttpContext.Request.Headers[DurabilityHeader]) is not { } homeDurable)
        {
            return Error(new SqlBatchFailure(new SqlError(SqlErrors.InvalidRequest, "TerraScale-Durability is home or local"), 0));
        }

        List<SqlPlannedCommand> statements = [.. commands.Where(command => command.Plan.Kind is SqlStatementKind.Read)];
        List<SqlPlannedCommand> every = [.. commands.Where(command => command.Plan.Kind is SqlStatementKind.Read or SqlStatementKind.Write)];
        var writes = every.Count > statements.Count;

        var reader = await replicas.OpenAsync(database, writes, preference, cancellationToken);
        SqlRequestTiming.Mark(HttpContext, "replica_open");
        if (reader.RunsAtHome(writes, preference))
        {
            WriteRoute? route = null;
            if (writes)
            {
                route = await router.RouteAsync(database, reader, [.. commands.Select(command => command.Command.Sql)], preference, homeDurable, cancellationToken);
                SqlRequestTiming.Mark(HttpContext, "route");
            }

            if (reader is not null)
            {
                await reader.DisposeAsync();
            }

            if (route?.Commit == WritePath.Local && !all && every.Count == commands.Count
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
                IReadOnlyList<SqlResult> results => TypedResults.Json(new SqlExecuteBody([.. results.Select(SqlStatementBody.Of)]),
                    SqlJsonContext.Default.SqlExecuteBody),
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
            || !ulong.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return Error(new SqlBatchFailure(new SqlError(SqlErrors.InvalidRequest, "the forwarded push position is malformed"), 0));
        }

        return await local.AppliedAsync(origin, database, sequence, cancellationToken)
            ? null
            : Error(new SqlBatchFailure(new SqlError(SqlErrors.Unavailable, "the region's earlier writes have not reached the home yet; retry"), 0));
    }

    private async ValueTask<IResult?> LocalAsync(
        TenantContext database, WriteRoute route, List<SqlPlannedCommand> statements, bool homeDurable, CancellationToken cancellationToken)
    {
        ILocalTransaction? transaction;
        try
        {
            transaction = await local.BeginAsync(database, cancellationToken);
            SqlRequestTiming.Mark(HttpContext, "local_begin");
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
            var outcome = await sql.ExecuteOnCopyAsync(database, statement => transaction.ExecuteAsync(statement, cancellationToken), statements, write: true, cancellationToken,
                (stage, milliseconds) => SqlRequestTiming.Record(HttpContext, stage, milliseconds));
            SqlRequestTiming.Mark(HttpContext, "local_execute");
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
                SqlRequestTiming.Mark(HttpContext, "ack");
                foreach (var timing in transaction.Timings)
                {
                    SqlRequestTiming.Record(HttpContext, timing.Key, timing.Value);
                }
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

            return TypedResults.Json(new SqlExecuteBody([.. results.Select(SqlStatementBody.Of)]),
                SqlJsonContext.Default.SqlExecuteBody);
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
            writing is not null && local.LastPushed(writing) is { } pushed ? $"{options.Region} {pushed}" : null, response =>
            {
                if (writing is not null && response.IsSuccessStatusCode && response.Headers.TryGetValues(ShapeOverlay.CommitHeader, out var committed))
                {
                    overlay.Forwarded(writing, string.Concat(committed));
                }
            });
        return Results.Empty;
    }

    private JsonResult Error(SqlBatchFailure failure)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        if (failure.Error.Home is { } home)
        {
            HttpContext.Response.Headers["X-TerraBase-Database-Home"] = home;
            HttpContext.Response.Headers.Location = HomeForwarding.RetryUrl(HttpContext, home);
            Listeners.ReplayIn(HttpContext, home);
        }

        return TypedResults.Json(SqlBatch.Body(failure, HttpContext.TraceIdentifier), SqlJsonContext.Default.SqlErrorBody, statusCode: failure.Error.Status);
    }
}
