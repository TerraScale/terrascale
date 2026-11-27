using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Consistency;

namespace TerraBase.Postgres.Sessions;

internal readonly union PgStatementResult(SqlResultSet, PgError);

internal sealed partial class PostgresConnection
{
    private static readonly FrozenSet<string> CatalogBooleans = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
    [
        "attnotnull", "atthasdef", "atthasmissing", "attisdropped", "attislocal", "attbyval", "relhasindex", "relisshared", "relhasrules", "relhastriggers",
        "relhassubclass", "relrowsecurity", "relforcerowsecurity", "relispopulated", "relispartition", "relhasoids", "indisunique", "indisprimary",
        "indisexclusion", "indimmediate", "indisclustered", "indisvalid", "indcheckxmin", "indisready", "indislive", "indisreplident", "indnullsnotdistinct",
        "typbyval", "typispreferred", "typisdefined", "typnotnull", "condeferrable", "condeferred", "convalidated", "conislocal", "connoinherit", "conperiod",
        "datistemplate", "datallowconn", "dathasloginevt", "rolsuper", "rolinherit", "rolcreaterole", "rolcreatedb", "rolcanlogin", "rolreplication",
        "rolbypassrls", "usecreatedb", "usesuper", "userepl", "usebypassrls", "hasindexes", "hasrules", "hastriggers", "rowsecurity", "pending_restart",
        "sametable", "polpermissive", "puballtables", "pubinsert", "pubupdate", "pubdelete", "pubtruncate", "pubviaroot", "tgisinternal", "tgdeferrable",
        "tginitdeferred", "inhdetachpending", "extrelocatable", "proretset", "proisstrict", "pg_table_is_visible", "pg_is_in_recovery",
    ]);

    private static readonly string[] SelectTags = BuildTags("SELECT ");
    private static readonly string[] UpdateTags = BuildTags("UPDATE ");
    private static readonly string[] DeleteTags = BuildTags("DELETE ");
    private static readonly string[] InsertTags = BuildTags("INSERT 0 ");

    private static string[] BuildTags(string prefix)
    {
        var tags = new string[17];
        for (var index = 0; index < tags.Length; index++)
        {
            tags[index] = prefix + index.ToString(CultureInfo.InvariantCulture);
        }

        return tags;
    }

    private async ValueTask<PgOutcome> GuardAsync(PgCommand command, IReadOnlyList<SqlValue> parameters, IReadOnlyList<PgColumn>? describedColumns)
    {
        using var scope = session.BeginStatement(closed);
        try
        {
            return await RunAsync(command, parameters, describedColumns, scope.Token);
        }
        catch (OperationCanceledException) when (!closed.IsCancellationRequested)
        {
            if (ShuttingDown)
            {
                return PgErrors.AdministratorShutdown;
            }

            if (Revoked)
            {
                return PgErrors.CredentialRevoked;
            }

            return scope.TimedOut ? PgErrors.StatementTimeout : PgErrors.QueryCanceled;
        }
    }

    private async ValueTask<PgOutcome> RunAsync(PgCommand command, IReadOnlyList<SqlValue> parameters, IReadOnlyList<PgColumn>? describedColumns, CancellationToken cancellationToken)
    {
        if (session.Transaction is { Failed: true } && command is not (PgCommitCommand or PgRollbackCommand or PgRollbackToCommand or PgEmptyCommand))
        {
            return PgError.InFailedTransaction;
        }

        return command switch
        {
            PgEmptyCommand => PgResult.EmptyQuery,
            PgSqlCommand sql => await ExecuteSqlAsync(sql, parameters, describedColumns, cancellationToken),
            PgCannedCommand canned => canned.Result,
            PgBeginCommand begin => Begin(begin),
            PgCommitCommand => await CommitAsync(cancellationToken),
            PgRollbackCommand => await RollbackAsync(),
            PgSavepointCommand savepoint => await SavepointAsync($"SAVEPOINT {savepoint.Name}", "SAVEPOINT", cancellationToken),
            PgReleaseCommand release => await SavepointAsync($"RELEASE SAVEPOINT {release.Name}", "RELEASE", cancellationToken),
            PgRollbackToCommand rollbackTo => await RollbackToAsync(rollbackTo, cancellationToken),
            PgSetCommand set => Set(set),
            PgResetAllCommand => ResetAll(),
            PgSetTransactionCommand setTransaction => SetTransaction(setTransaction),
            PgSetConfigCommand setConfig => SetConfig(setConfig),
            PgShowCommand show => Show(show),
            PgDeallocateCommand deallocate => Deallocate(deallocate),
            PgDiscardCommand => await DiscardAsync(),
            PgListenCommand listen => Listen(listen),
            PgUnlistenCommand unlisten => await UnlistenAsync(unlisten),
            PgNoOpCommand noOp => PgResult.Command(noOp.Tag),
            PgCopyFromCommand copyFrom => await CopyFromAsync(copyFrom, cancellationToken),
            PgCopyToCommand copyTo => await CopyToAsync(copyTo, parameters, cancellationToken),
        };
    }

    private async ValueTask<PgOutcome> ExecuteSqlAsync(PgSqlCommand command, IReadOnlyList<SqlValue> parameters, IReadOnlyList<PgColumn>? describedColumns, CancellationToken cancellationToken)
    {
        if (parameters.Count < command.Rewrite.ParameterCount)
        {
            return new PgError("42P02", $"there is no parameter ${(parameters.Count + 1).ToString(CultureInfo.InvariantCulture)}");
        }

        if (command.Access == SqlAccess.Write && session.Grant.MaxAccess == SqlAccess.Read)
        {
            return PgErrors.ReadOnlyCredential;
        }

        var statement = new SqlStatement(command.Rewrite.Sql, command.Rewrite.Bind(parameters), NoNamed);
        var executed = await ExecuteStatementAsync(statement, command.Access, cancellationToken);
        if (command.Verb is "CREATE" or "ALTER" or "DROP")
        {
            session.InvalidateSchemaCache();
        }

        return executed switch
        {
            SqlResultSet set => ToResult(command, set, describedColumns),
            PgError error => error,
        };
    }

    private async ValueTask<PgStatementResult> ExecuteStatementAsync(SqlStatement statement, SqlAccess access, CancellationToken cancellationToken)
    {
        if (session.Transaction is { } block)
        {
            if (access == SqlAccess.Write && block.Access == SqlAccess.Read)
            {
                return PgErrors.ReadOnlyTransaction;
            }

            var ensured = await EnsureTransactionAsync(block, access, cancellationToken);
            return ensured switch
            {
                ISqlTransaction active => await ExecuteInTransactionAsync(active, statement, cancellationToken),
                PgError beginError => beginError,
            };
        }

        var request = new SqlStatementRequest(session.Executor.DatabaseId, [statement], access, session.Settings.DefaultMode, session.Bookmark);
        if (access == SqlAccess.Read)
        {
            var read = await session.Executor.ReadAsync(request, cancellationToken);
            return read switch
            {
                SqlReadResult result => ObservedRead(result),
                SqlError error => PgErrors.From(error),
                StorageBusy busy => PgErrors.From(busy),
                StorageTimeout timeout => PgErrors.From(timeout),
            };
        }

        var written = await session.Executor.WriteAsync(request, cancellationToken);
        return written switch
        {
            SqlWriteResult write => ObservedWrite(write),
            SqlError error => PgErrors.From(error),
            SerializationConflict conflict => PgErrors.From(conflict),
            SchemaMismatch mismatch => ObservedSchemaMismatch(mismatch),
            ClusterUnavailable unavailable => PgErrors.From(unavailable),
            ClusterTimeout timeout => PgErrors.From(timeout),
            StorageBusy busy => PgErrors.From(busy),
        };
    }

    private PgStatementResult ObservedRead(SqlReadResult result)
    {
        session.ObserveCurrentSchemaEpoch();
        return Observed(result.Observed, result.ResultSets);
    }

    private PgStatementResult ObservedWrite(SqlWriteResult write)
    {
        session.ObserveSchemaEpoch(write.Commit.SchemaEpoch);
        return Observed(write.Commit.Bookmark, write.ResultSets);
    }

    private PgStatementResult ObservedSchemaMismatch(SchemaMismatch mismatch)
    {
        session.ObserveSchemaEpoch(mismatch.ActualEpoch);
        return PgErrors.From(mismatch);
    }

    private async ValueTask<PgStatementResult> ExecuteInTransactionAsync(ISqlTransaction active, SqlStatement statement, CancellationToken cancellationToken)
    {
        var executed = await active.ExecuteAsync(statement, cancellationToken);
        session.ObserveCurrentSchemaEpoch();
        return executed switch
        {
            SqlResultSet set => set,
            SqlError error => PgErrors.From(error),
            StorageBusy busy => PgErrors.From(busy),
            StorageTimeout timeout => PgErrors.From(timeout),
        };
    }

    private PgStatementResult Observed(CausalBookmark bookmark, IReadOnlyList<SqlResultSet> sets)
    {
        session.Observe(bookmark);
        return sets.Count > 0 ? sets[0] : new SqlResultSet([], [], 0, 0);
    }

    private static PgResult ToResult(PgSqlCommand command, SqlResultSet set, IReadOnlyList<PgColumn>? describedColumns)
    {
        if (command.Explain)
        {
            var detail = Math.Max(0, set.Columns.Count - 1);
            IReadOnlyList<IReadOnlyList<SqlValue>> plan = [.. set.Rows.Select(row => (IReadOnlyList<SqlValue>)[row.Count > detail ? row[detail] : SqlValue.Null])];
            return new PgResult([new PgColumn("QUERY PLAN", PgOid.Text)], plan, "EXPLAIN");
        }

        IReadOnlyList<PgColumn>? columns = set.Columns.Count == 0 ? null : InferColumns(set.Columns, set.Rows, command.Rewrite.UsesCatalog);
        if (describedColumns is { } described && columns is { } actual && described.Count == actual.Count)
        {
            columns = described;
        }

        return new PgResult(columns, set.Rows, Tag(command, set));
    }

    private static string Tag(PgSqlCommand command, SqlResultSet set)
    {
        if (command.TagOverride is { } tag)
        {
            return tag;
        }

        return command.Verb switch
        {
            "INSERT" or "REPLACE" => (ulong)set.RowsAffected < (ulong)InsertTags.Length ? InsertTags[set.RowsAffected] : $"INSERT 0 {set.RowsAffected.ToString(CultureInfo.InvariantCulture)}",
            "UPDATE" => (ulong)set.RowsAffected < (ulong)UpdateTags.Length ? UpdateTags[set.RowsAffected] : $"UPDATE {set.RowsAffected.ToString(CultureInfo.InvariantCulture)}",
            "DELETE" => (ulong)set.RowsAffected < (ulong)DeleteTags.Length ? DeleteTags[set.RowsAffected] : $"DELETE {set.RowsAffected.ToString(CultureInfo.InvariantCulture)}",
            "MERGE" => $"MERGE {set.RowsAffected.ToString(CultureInfo.InvariantCulture)}",
            "SELECT" or "VALUES" or "WITH" or "PRAGMA" => (uint)set.Rows.Count < (uint)SelectTags.Length ? SelectTags[set.Rows.Count] : $"SELECT {set.Rows.Count.ToString(CultureInfo.InvariantCulture)}",
            var verb => verb,
        };
    }

    internal static IReadOnlyList<PgColumn> InferColumns(IReadOnlyList<SqlColumn> columns, IReadOnlyList<IReadOnlyList<SqlValue>> rows, bool catalog)
    {
        var inferred = new PgColumn[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var oid = PgOid.FromDeclaredType(column.DeclaredType);
            if (oid == 0)
            {
                var kind = SqlValueKind.Null;
                foreach (var row in rows)
                {
                    if (index < row.Count && row[index].Kind != SqlValueKind.Null)
                    {
                        kind = row[index].Kind;
                        break;
                    }
                }

                oid = kind switch
                {
                    SqlValueKind.Integral when catalog && CatalogBooleans.Contains(column.Name) => PgOid.Bool,
                    SqlValueKind.Integral => PgOid.Int8,
                    SqlValueKind.Real => PgOid.Float8,
                    SqlValueKind.Blob => PgOid.Bytea,
                    SqlValueKind.Text => PgOid.Text,
                    SqlValueKind.Null => ExpressionOid(column.Name),
                    _ => throw new UnreachableException(),
                };
            }

            inferred[index] = new PgColumn(column.Name, oid);
        }

        return inferred;
    }

    private static int ExpressionOid(string name)
    {
        var lowered = name.ToLowerInvariant();
        if (lowered.StartsWith("count(", StringComparison.Ordinal) || lowered.StartsWith("length(", StringComparison.Ordinal)
            || lowered.StartsWith("row_number(", StringComparison.Ordinal) || lowered.StartsWith("rank(", StringComparison.Ordinal)
            || lowered.StartsWith("dense_rank(", StringComparison.Ordinal) || lowered.StartsWith("instr(", StringComparison.Ordinal))
        {
            return PgOid.Int8;
        }

        if (lowered.StartsWith("avg(", StringComparison.Ordinal) || lowered.StartsWith("total(", StringComparison.Ordinal))
        {
            return PgOid.Float8;
        }

        var cast = lowered.LastIndexOf(" as ", StringComparison.Ordinal);
        return lowered.StartsWith("cast(", StringComparison.Ordinal) && cast > 0 && lowered.EndsWith(')')
            ? PgOid.FromDeclaredType(lowered[(cast + 4)..^1]) is > 0 and var castOid ? castOid : PgOid.Text
            : PgOid.Text;
    }
}
