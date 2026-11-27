using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Rules;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Features.Rows;

internal readonly union RowKeyParse(long, InvalidInput);

internal readonly union RowPlanOutcome(PlannedRow, RowRefused, SqlRefusal);

internal readonly union RowClausesOutcome(IReadOnlyList<string>, SqlRefusal);

internal readonly union RowConditionOutcome(string, SqlRefusal);

internal enum RowAction
{
    Create,
    Read,
    Update,
    Delete,
}

internal sealed record RowCommand(RowAction Action, string? Table, long? Key, IReadOnlyDictionary<string, JsonElement>? Values);

internal sealed record PlannedRow(SqlStatement Write, SqlStatement? Check);

internal sealed class RowsService(DatabaseAccess access, RulesStore rules, IClusterCoordinator coordinator, SqlRunner runner, TimeProvider time)
{
    public const string Rows = "/v1/projects/{project}/databases/{database}/tables/{table}/rows";

    public const string Row = Rows + "/{key}";

    public const string Batch = "/v1/projects/{project}/databases/{database}/rows:batch";

    public const int MaxOperations = 100;

    private const int MaxTableCacheEntries = 4096;

    private static readonly ConditionalWeakTable<IReadOnlyList<string>, string> ReadableColumnsCache = new();

    private readonly ConcurrentDictionary<TableCacheKey, TableMetadata> _tableCache = new(TableCacheKeyComparer.Instance);

    private int _tableCacheCount;

    public async ValueTask<RowsOutcome> RunAsync(
        Caller caller, string? project, string? database, IReadOnlyList<RowCommand> commands, bool batch, CancellationToken cancellationToken)
    {
        var outcome = await access.DataAsync(caller, project, database, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase target => await access.OpenAsync(target.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => await ExecuteAsync(target, session, commands, batch, cancellationToken),
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public static RowKeyParse Key(string? raw) =>
        long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key) ? key : new InvalidInput("key must be the row's integer key", "invalid_key");

    private async ValueTask<RowsOutcome> ExecuteAsync(
        ResolvedDatabase target, ISqlDatabaseSession session, IReadOnlyList<RowCommand> commands, bool batch, CancellationToken cancellationToken)
    {
        var writes = HasWrites(commands);
        if (writes && !target.CanWrite)
        {
            return SqlRefusal.ForbiddenScope(0);
        }

        var set = target.EndUser is null ? null : await rules.CurrentAsync(target.Database.Id, cancellationToken) switch
        {
            StoredRules current => current.Rules,
            ResourceNotFound or BackendUnavailable => new RulesSet(new Dictionary<string, RulesTable>()),
        };
        var now = time.GetUtcNow();
        var renderTime = now.ToUnixTimeMilliseconds();
        var planned = new PlannedRow[commands.Count];
        string? lastTable = null;
        TableMetadata? lastMeta = null;
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index].Table is not { Length: > 0 } table)
            {
                return new InvalidInput("table names the table", "invalid_table");
            }

            TableMetadata? meta;
            if (lastMeta is not null && lastTable is not null && AsciiIgnoreCaseComparer.Instance.Equals(lastTable, table))
            {
                meta = lastMeta;
            }
            else
            {
                meta = await GetTableMetadataAsync(session, table, cancellationToken);
                if (meta is null)
                {
                    return new ResourceNotFound(string.Concat("the database has no table ", table), "unknown_table");
                }

                lastTable = table;
                lastMeta = meta;
            }

            var built = Build(commands[index], table, meta, set, target.EndUser, now, renderTime, new Position(index, batch));
            var failure = built switch
            {
                PlannedRow row => Assign(planned, index, row),
                RowRefused refused => new RowsOutcome(refused),
                SqlRefusal refusal => new RowsOutcome(refusal),
            };
            if (failure is { } refusedOutcome)
            {
                return refusedOutcome;
            }
        }

        if (await ValidateAsync(session, target, commands, planned, batch, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        if (writes && batch)
        {
            return await ExecuteBatchWriteAsync(target, session, commands, planned, cancellationToken);
        }

        var writeStatements = new SqlStatement[planned.Length];
        for (var i = 0; i < planned.Length; i++)
        {
            writeStatements[i] = planned[i].Write;
        }

        var request = new SqlStatementRequest(target.Database.Id, writeStatements, writes ? SqlAccess.Write : SqlAccess.Read,
            target.Database.DefaultConsistency, CausalBookmark.Empty);
        var outcome = writes
            ? await coordinator.ExecuteWriteAsync(session, request, cancellationToken) switch
            {
                SqlWriteResult written => Answers(target, written.ResultSets, commands, batch, wrote: true),
                SqlError error => new RowsOutcome(SqlRefusal.From(error)),
                SerializationConflict conflict => new RowsOutcome(SqlRefusal.From(conflict)),
                SchemaMismatch mismatch => new RowsOutcome(SqlRefusal.From(mismatch)),
                ClusterUnavailable unavailable => new RowsOutcome(SqlRefusal.From(unavailable)),
                ClusterTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
                StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
            }
            : await session.ExecuteReadAsync(request, cancellationToken) switch
            {
                SqlReadResult read => Answers(target, read.ResultSets, commands, batch, wrote: false),
                SqlError error => new RowsOutcome(SqlRefusal.From(error)),
                StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
                StorageTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
            };
        return outcome;
    }

    private ValueTask<TableMetadata?> GetTableMetadataAsync(ISqlDatabaseSession session, string table, CancellationToken cancellationToken)
    {
        var epoch = session.SchemaEpoch;
        var key = new TableCacheKey(session.DatabaseId, table, epoch);
        if (_tableCache.TryGetValue(key, out var cached))
        {
            return ValueTask.FromResult<TableMetadata?>(cached);
        }

        return LoadTableMetadataAsync(session, table, epoch, key, cancellationToken);
    }

    private async ValueTask<TableMetadata?> LoadTableMetadataAsync(
        ISqlDatabaseSession session, string table, ulong epoch, TableCacheKey key, CancellationToken cancellationToken)
    {
        var raw = await RulesService.TableColumnsAsync(session, table, cancellationToken);
        if (raw.Count == 0)
        {
            return null;
        }

        var columns = new TableColumn[raw.Count];
        var names = new string[raw.Count];
        var quotedNames = new string[raw.Count];
        var columnIndex = new Dictionary<string, int>(raw.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < raw.Count; i++)
        {
            var col = raw[i];
            columns[i] = col;
            names[i] = col.Name;
            quotedNames[i] = RulesCompiler.Quote(col.Name);
            columnIndex.TryAdd(col.Name, i);
        }

        var metadata = new TableMetadata(columns, names, quotedNames, RulesCompiler.Quote(table), columnIndex);
        if (session.SchemaEpoch == epoch)
        {
            StoreTableMetadata(key, metadata);
        }

        return metadata;
    }

    private void StoreTableMetadata(TableCacheKey key, TableMetadata metadata)
    {
        if (!_tableCache.ContainsKey(key) && Volatile.Read(ref _tableCacheCount) >= MaxTableCacheEntries)
        {
            _tableCache.Clear();
            Volatile.Write(ref _tableCacheCount, 0);
        }

        if (_tableCache.TryAdd(key, metadata))
        {
            Interlocked.Increment(ref _tableCacheCount);
        }
        else
        {
            _tableCache[key] = metadata;
        }
    }

    private static bool HasWrites(IReadOnlyList<RowCommand> commands)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            if (commands[i].Action != RowAction.Read)
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<RowsOutcome> ExecuteBatchWriteAsync(
        ResolvedDatabase target, ISqlDatabaseSession session, IReadOnlyList<RowCommand> commands, PlannedRow[] planned, CancellationToken cancellationToken)
    {
        var begun = await coordinator.BeginTransactionAsync(session, new SqlTransactionOptions(target.Database.DefaultConsistency, SqlAccess.Write, CausalBookmark.Empty), cancellationToken);
        return begun switch
        {
            ISqlTransaction transaction => await RunBatchTransactionAsync(target, session, transaction, commands, planned, cancellationToken),
            StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
            ClusterTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
        };
    }

    private async ValueTask<RowsOutcome> RunBatchTransactionAsync(
        ResolvedDatabase target, ISqlDatabaseSession session, ISqlTransaction transaction, IReadOnlyList<RowCommand> commands, PlannedRow[] planned, CancellationToken cancellationToken)
    {
        await using (transaction)
        {
            var results = new SqlResultSet[planned.Length];
            for (var index = 0; index < planned.Length; index++)
            {
                var step = await transaction.ExecuteAsync(planned[index].Write, cancellationToken);
                var failure = step switch
                {
                    SqlResultSet result => CollectOrRefuse(results, result, commands[index], index),
                    SqlError error => new RowsOutcome(SqlRefusal.From(error)),
                    StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
                    StorageTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
                };
                if (failure is { } refused)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return refused;
                }
            }

            var committed = await coordinator.CommitTransactionAsync(session, transaction, cancellationToken);
            return committed switch
            {
                CommitMetadata => Answers(target, results, commands, batch: true, wrote: true),
                SerializationConflict conflict => new RowsOutcome(SqlRefusal.From(conflict)),
                SchemaMismatch mismatch => new RowsOutcome(SqlRefusal.From(mismatch)),
                ClusterUnavailable unavailable => new RowsOutcome(SqlRefusal.From(unavailable)),
                ClusterTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
                StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
            };
        }
    }

    private static RowsOutcome? CollectOrRefuse(SqlResultSet[] results, SqlResultSet result, RowCommand command, int index)
    {
        if (result.Rows.Count == 0)
        {
            return new RowRefused(
                command.Action == RowAction.Create ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound,
                command.Action == RowAction.Create ? "rule_denied" : "row_not_found",
                command.Action == RowAction.Create ? "the rules refused the row" : "the row does not exist or the rules hide it",
                new RowsErrorDetails(Table: command.Table, Index: index));
        }

        results[index] = result;
        return null;
    }

    private static async ValueTask<RowsOutcome?> ValidateAsync(
        ISqlDatabaseSession session, ResolvedDatabase target, IReadOnlyList<RowCommand> commands, PlannedRow[] planned, bool batch,
        CancellationToken cancellationToken)
    {
        var checkCount = 0;
        for (var index = 0; index < planned.Length; index++)
        {
            if (planned[index].Check is not null)
            {
                checkCount++;
            }
        }

        if (checkCount == 0)
        {
            return null;
        }

        var checkIndices = new int[checkCount];
        var checkStatements = new SqlStatement[checkCount];
        var next = 0;
        for (var index = 0; index < planned.Length; index++)
        {
            if (planned[index].Check is { } check)
            {
                checkIndices[next] = index;
                checkStatements[next] = check;
                next++;
            }
        }

        var request = new SqlStatementRequest(target.Database.Id, checkStatements, SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty);
        var read = await session.ExecuteReadAsync(request, cancellationToken);
        return read switch
        {
            SqlReadResult answered => Checked(answered.ResultSets, checkIndices, commands, batch),
            SqlError error => new RowsOutcome(SqlRefusal.From(error)),
            StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
            StorageTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
        };
    }

    private static RowsOutcome? Checked(IReadOnlyList<SqlResultSet> results, int[] checkIndices, IReadOnlyList<RowCommand> commands, bool batch)
    {
        for (var at = 0; at < checkIndices.Length && at < results.Count; at++)
        {
            if (results[at].Rows is not [var row, ..] || !IsTrue(row[0]))
            {
                continue;
            }

            for (var clause = 1; clause < row.Count; clause++)
            {
                if (!IsTrue(row[clause]))
                {
                    var index = checkIndices[at];
                    return new RowsOutcome(new RowRefused(StatusCodes.Status400BadRequest, "validation_failed",
                        string.Concat("validate[", (clause - 1).ToString(CultureInfo.InvariantCulture), "] is false for the written row"),
                        new RowsErrorDetails(Table: commands[index].Table, Index: batch ? index : null, Clause: clause - 1)));
                }
            }
        }

        return null;
    }

    private static bool IsTrue(SqlValue value) => value.TryGetIntegral(out var integer) && integer != 0;

    private RowsOutcome Answers(ResolvedDatabase target, IReadOnlyList<SqlResultSet> results, IReadOnlyList<RowCommand> commands, bool batch, bool wrote)
    {
        runner.Meter(target, results, wrote, target.Database.DefaultConsistency);
        var count = Math.Min(commands.Count, results.Count);
        var answers = new RowAnswer[count];
        for (var index = 0; index < count; index++)
        {
            var result = results[index];
            if (result.Rows.Count == 0)
            {
                return new RowRefused(
                    commands[index].Action == RowAction.Create ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound,
                    commands[index].Action == RowAction.Create ? "rule_denied" : "row_not_found",
                    commands[index].Action == RowAction.Create ? "the rules refused the row" : "the row does not exist or the rules hide it",
                    new RowsErrorDetails(Table: commands[index].Table, Index: batch ? index : null));
            }

            var row = result.Rows[0];
            var key = row[0].TryGetIntegral(out var rowId) ? rowId.ToString(CultureInfo.InvariantCulture) : SqlBuild.Format(row[0]);
            IReadOnlyList<KeyValuePair<string, SqlValue>>? values;
            if (commands[index].Action == RowAction.Delete)
            {
                values = null;
            }
            else
            {
                var valueCount = result.Columns.Count - 1;
                if (valueCount <= 0)
                {
                    values = [];
                }
                else
                {
                    var pairs = new KeyValuePair<string, SqlValue>[valueCount];
                    for (var at = 0; at < valueCount; at++)
                    {
                        pairs[at] = new KeyValuePair<string, SqlValue>(result.Columns[at + 1].Name, row[at + 1]);
                    }

                    values = pairs;
                }
            }

            answers[index] = new RowAnswer(key, values);
        }

        return new RowsOutcome(answers);
    }

    private readonly record struct Position(int Index, bool Batch)
    {
        public RowsErrorDetails Details(string table, string column) => new(Table: table, Column: column, Index: Batch ? Index : null);
    }

    private static RowPlanOutcome Build(
        RowCommand command, string tableName, TableMetadata meta, RulesSet? set, RulesAuth? auth, DateTimeOffset now, long renderTime, Position position)
    {
        var columns = meta.Names;
        var rule = set?.Table(tableName);
        var table = meta.QuotedTable;
        var readable = rule?.Columns is { Count: > 0 } chosen ? CachedReadableColumns(chosen) : "*";
        if (set is not null && auth is not null && rule is null)
        {
            return SqlRefusal.RuleDenied(string.Concat("the rules do not cover ", tableName), position.Index);
        }

        var assigned = command.Action switch
        {
            RowAction.Create => rule?.Set?.Create,
            RowAction.Update => rule?.Set?.Update,
            RowAction.Read or RowAction.Delete => null,
            _ => throw new UnreachableException(),
        };

        List<(int ColumnIndex, string Column, SqlValue Value)>? values = null;
        if (command.Values is { Count: > 0 } rawValues)
        {
            values = new List<(int ColumnIndex, string Column, SqlValue Value)>(rawValues.Count + (assigned?.Count ?? 0));
            foreach (var (column, element) in rawValues)
            {
                if (!meta.ColumnIndex.TryGetValue(column, out var columnIdx))
                {
                    return new RowRefused(StatusCodes.Status400BadRequest, "unknown_column", string.Concat("the table ", tableName, " has no column ", column),
                        position.Details(tableName, column));
                }

                var known = columns[columnIdx];
                if (SqlWire.Value(element) is not { } value)
                {
                    return new RowRefused(StatusCodes.Status400BadRequest, "invalid_request", string.Concat(known, " is not a SQL value"), position.Details(tableName, known));
                }

                if (auth is not null && (!ContainsIgnoreCase(rule?.Writable, known) || ContainsKeyIgnoreCase(assigned, known)))
                {
                    return new RowRefused(StatusCodes.Status403Forbidden, "column_not_writable", string.Concat(known, " is not writable"), position.Details(tableName, known));
                }

                values.Add((columnIdx, known, value));
            }
        }

        if (auth is not null && assigned is { Count: > 0 })
        {
            values ??= new List<(int ColumnIndex, string Column, SqlValue Value)>(assigned.Count);
            var ulid = Ulid.NewUlid(now);
            foreach (var (column, expression) in assigned)
            {
                for (var i = values.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(values[i].Column, column, StringComparison.OrdinalIgnoreCase))
                    {
                        values.RemoveAt(i);
                    }
                }

                var columnIdx = meta.ColumnIndex.TryGetValue(column, out var found) ? found : -1;
                values.Add((columnIdx, column, RulesCompiler.SetValue(expression, auth, renderTime, ulid)));
            }
        }

        return command.Action switch
        {
            RowAction.Read => Condition(rule?.Read, auth, RulesOperation.Read, columns, position.Index, renderTime) switch
            {
                string condition => new PlannedRow(
                    SqlBuild.Statement(string.Concat("SELECT rowid, ", readable, " FROM ", table, " WHERE rowid = ? AND ", condition), SqlBuild.Integer(command.Key ?? 0)), null),
                SqlRefusal refusal => refusal,
            },
            RowAction.Delete => Condition(rule?.Delete, auth, RulesOperation.Delete, columns, position.Index, renderTime) switch
            {
                string condition => new PlannedRow(
                    SqlBuild.Statement(string.Concat("DELETE FROM ", table, " WHERE rowid = ? AND ", condition, " RETURNING rowid"), SqlBuild.Integer(command.Key ?? 0)), null),
                SqlRefusal refusal => refusal,
            },
            RowAction.Create => Create(table, readable, values, rule, auth, meta, position.Index, renderTime),
            RowAction.Update => Update(table, readable, command.Key ?? 0, values, rule, auth, meta, position.Index, renderTime),
            _ => throw new UnreachableException(),
        };
    }

    private static string CachedReadableColumns(IReadOnlyList<string> columns) =>
        ReadableColumnsCache.GetValue(columns, static list =>
        {
            var sb = new StringBuilder();
            for (var i = 0; i < list.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(RulesCompiler.Quote(list[i]));
            }

            return sb.ToString();
        });

    private static bool ContainsIgnoreCase(IReadOnlyList<string>? list, string value)
    {
        if (list is null)
        {
            return false;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsKeyIgnoreCase(IReadOnlyDictionary<string, string>? dictionary, string key)
    {
        if (dictionary is null)
        {
            return false;
        }

        if (dictionary.ContainsKey(key))
        {
            return true;
        }

        foreach (var existing in dictionary.Keys)
        {
            if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static RowPlanOutcome Create(
        string table, string readable, List<(int ColumnIndex, string Column, SqlValue Value)>? values, RulesTable? rule, RulesAuth? auth, TableMetadata meta,
        int index, long renderTime)
    {
        if (values is null || values.Count == 0)
        {
            return SqlRefusal.Invalid("row names at least one column", index);
        }

        var tableColumns = meta.Columns;
        var quotedNames = meta.QuotedNames;
        Span<int> valueByColumn = tableColumns.Length <= 64 ? stackalloc int[tableColumns.Length] : new int[tableColumns.Length];
        valueByColumn.Fill(-1);

        var namesBuilder = new StringBuilder(values.Count * 12);
        var boundCount = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var item = values[i];
            if (i > 0)
            {
                namesBuilder.Append(", ");
            }

            if (item.ColumnIndex >= 0)
            {
                namesBuilder.Append(quotedNames[item.ColumnIndex]);
                if (valueByColumn[item.ColumnIndex] < 0)
                {
                    valueByColumn[item.ColumnIndex] = i;
                    boundCount++;
                }
            }
            else
            {
                namesBuilder.Append(RulesCompiler.Quote(item.Column));
            }
        }

        var names = namesBuilder.ToString();
        var bound = new SqlValue[boundCount];
        var nextBound = 0;
        var projectedBuilder = new StringBuilder(tableColumns.Length * 16);
        for (var c = 0; c < tableColumns.Length; c++)
        {
            if (c > 0)
            {
                projectedBuilder.Append(", ");
            }

            var matched = valueByColumn[c];
            if (matched >= 0)
            {
                projectedBuilder.Append("? AS ");
                projectedBuilder.Append(quotedNames[c]);
                bound[nextBound++] = values[matched].Value;
            }
            else if (tableColumns[c].DefaultSql is { Length: > 0 } defaultSql)
            {
                projectedBuilder.Append('(');
                projectedBuilder.Append(defaultSql);
                projectedBuilder.Append(") AS ");
                projectedBuilder.Append(quotedNames[c]);
            }
            else
            {
                projectedBuilder.Append("NULL AS ");
                projectedBuilder.Append(quotedNames[c]);
            }
        }

        var projected = projectedBuilder.ToString();
        var condition = Condition(rule?.Create, auth, RulesOperation.Create, meta.Names, index, renderTime, newPrefix: "new.");
        var validations = Validations(rule, auth, meta.Names, index, renderTime);
        return condition switch
        {
            string allowed => validations switch
            {
                IReadOnlyList<string> clauses => new PlannedRow(
                    SqlBuild.Statement(string.Concat("INSERT INTO ", table, " (", names, ") SELECT ", names, " FROM (SELECT ", projected, ") AS new WHERE ",
                        Conjunction(allowed, clauses), " RETURNING rowid, ", readable), bound),
                    clauses.Count == 0 ? null : SqlBuild.Statement(string.Concat("SELECT ", allowed, ", ", string.Join(", ", clauses), " FROM (SELECT ", projected, ") AS new"), bound)),
                SqlRefusal refusal => refusal,
            },
            SqlRefusal refusal => refusal,
        };
    }

    private static RowPlanOutcome Update(
        string table, string readable, long key, List<(int ColumnIndex, string Column, SqlValue Value)>? values, RulesTable? rule, RulesAuth? auth, TableMetadata meta,
        int index, long renderTime)
    {
        if (values is null || values.Count == 0)
        {
            return SqlRefusal.Invalid("row names at least one column", index);
        }

        var columns = meta.Names;
        var quotedNames = meta.QuotedNames;
        Span<int> exactValueByColumn = columns.Length <= 64 ? stackalloc int[columns.Length] : new int[columns.Length];
        exactValueByColumn.Fill(-1);

        var assignmentsBuilder = new StringBuilder(values.Count * 16);
        var nextArgCount = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var item = values[i];
            if (i > 0)
            {
                assignmentsBuilder.Append(", ");
            }

            if (item.ColumnIndex >= 0 && string.Equals(item.Column, columns[item.ColumnIndex], StringComparison.Ordinal))
            {
                assignmentsBuilder.Append(quotedNames[item.ColumnIndex]);
                if (exactValueByColumn[item.ColumnIndex] < 0)
                {
                    exactValueByColumn[item.ColumnIndex] = i;
                    nextArgCount++;
                }
            }
            else
            {
                assignmentsBuilder.Append(RulesCompiler.Quote(item.Column));
            }

            assignmentsBuilder.Append(" = ?");
        }

        var assignments = assignmentsBuilder.ToString();
        var nextBuilder = new StringBuilder(columns.Length * 24);
        var args = new SqlValue[values.Count + 1 + nextArgCount];
        for (var i = 0; i < values.Count; i++)
        {
            args[i] = values[i].Value;
        }

        args[values.Count] = SqlBuild.Integer(key);
        var nextArgPos = values.Count + 1;
        for (var c = 0; c < columns.Length; c++)
        {
            if (c > 0)
            {
                nextBuilder.Append(", ");
            }

            var matched = exactValueByColumn[c];
            if (matched >= 0)
            {
                nextBuilder.Append("? AS ");
                nextBuilder.Append(quotedNames[c]);
                args[nextArgPos++] = values[matched].Value;
            }
            else
            {
                nextBuilder.Append(table);
                nextBuilder.Append('.');
                nextBuilder.Append(quotedNames[c]);
                nextBuilder.Append(" AS ");
                nextBuilder.Append(quotedNames[c]);
            }
        }

        var next = nextBuilder.ToString();
        var (compiledRule, compileFailure) = CompileUpdateCondition(rule?.Update, auth, columns, index);
        if (compileFailure is not null)
        {
            return compileFailure;
        }

        return Validations(rule, auth, columns, index, renderTime) switch
        {
            IReadOnlyList<string> clauses => BuildUpdatePlan(table, readable, key, assignments, next, args, values.Count, nextArgCount, compiledRule, auth, clauses, renderTime),
            SqlRefusal refusal => refusal,
        };
    }

    private static (CompiledCondition? Compiled, SqlRefusal? Failure) CompileUpdateCondition(
        string? condition, RulesAuth? auth, IReadOnlyList<string> columns, int index)
    {
        if (auth is null)
        {
            return (null, null);
        }

        if (condition is null)
        {
            return (null, SqlRefusal.RuleDenied("the rules do not allow this operation", index));
        }

        return RulesCompiler.Compile(condition, columns, RulesOperation.Update) switch
        {
            CompiledCondition compiled => (compiled, null),
            InvalidInput refused => (null, new SqlRefusal("rules_invalid", refused.Message, "42501", index, StatusCodes.Status403Forbidden)),
        };
    }

    private static PlannedRow BuildUpdatePlan(
        string table,
        string readable,
        long key,
        string assignments,
        string next,
        SqlValue[] args,
        int valueCount,
        int nextArgCount,
        CompiledCondition? compiledRule,
        RulesAuth? auth,
        IReadOnlyList<string> clauses,
        long renderTime)
    {
        var allowed = compiledRule is not null && auth is not null
            ? RulesCompiler.Render(compiledRule, auth, renderTime, newPrefix: "new.", rowPrefix: string.Concat(table, "."))
            : "1";
        SqlStatement? checkStatement = null;
        if (clauses.Count > 0)
        {
            var checkedAllowed = compiledRule is not null && auth is not null
                ? RulesCompiler.Render(compiledRule, auth, renderTime, newPrefix: "new.", rowPrefix: "row_values.")
                : "1";
            var checkArgs = new SqlValue[nextArgCount + 2];
            checkArgs[0] = SqlBuild.Integer(key);
            Array.Copy(args, valueCount + 1, checkArgs, 1, nextArgCount);
            checkArgs[nextArgCount + 1] = SqlBuild.Integer(key);
            checkStatement = SqlBuild.Statement(string.Concat("SELECT ", checkedAllowed, ", ", string.Join(", ", clauses), " FROM (SELECT * FROM ",
                table, " WHERE rowid = ?) AS row_values, (SELECT ", next, " FROM ", table, " WHERE rowid = ?) AS new"), checkArgs);
        }

        return new PlannedRow(
            SqlBuild.Statement(string.Concat("UPDATE ", table, " SET ", assignments, " WHERE rowid = ? AND EXISTS (SELECT 1 FROM (SELECT ", next,
                ") AS new WHERE ", Conjunction(allowed, clauses), ") RETURNING rowid, ", readable), args),
            checkStatement);
    }

    private static string Conjunction(string condition, IReadOnlyList<string> clauses) =>
        clauses.Count == 0 ? condition : string.Concat(condition, " AND ", string.Join(" AND ", clauses));

    private static RowClausesOutcome Validations(RulesTable? rule, RulesAuth? auth, IReadOnlyList<string> columns, int index, long renderTime)
    {
        if (auth is null || rule?.Validate is not { Count: > 0 } clauses)
        {
            return Clauses([]);
        }

        var rendered = new string[clauses.Count];
        for (var i = 0; i < clauses.Count; i++)
        {
            var compiled = RulesCompiler.Compile(clauses[i], columns, RulesOperation.Validate) switch
            {
                CompiledCondition ready => ready,
                InvalidInput => null,
            };
            if (compiled is null)
            {
                return new SqlRefusal("rules_invalid", "a validate clause no longer compiles against the table", "42501", index, StatusCodes.Status403Forbidden);
            }

            rendered[i] = RulesCompiler.Render(compiled, auth, renderTime, "new.");
        }

        return Clauses(rendered);
    }

    private static RowClausesOutcome Clauses(IReadOnlyList<string> clauses) => new(clauses);

    private static RowConditionOutcome Condition(
        string? condition, RulesAuth? auth, RulesOperation operation, IReadOnlyList<string> columns, int index, long renderTime, string? newPrefix = null,
        string? rowPrefix = null)
    {
        if (auth is null)
        {
            return "1";
        }

        if (condition is null)
        {
            return SqlRefusal.RuleDenied("the rules do not allow this operation", index);
        }

        return RulesCompiler.Compile(condition, columns, operation) switch
        {
            CompiledCondition compiled => RulesCompiler.Render(compiled, auth, renderTime, newPrefix, rowPrefix),
            InvalidInput refused => new SqlRefusal("rules_invalid", refused.Message, "42501", index, StatusCodes.Status403Forbidden),
        };
    }

    private static RowsOutcome? Assign(PlannedRow[] planned, int index, PlannedRow row)
    {
        planned[index] = row;
        return null;
    }

    private readonly record struct TableCacheKey(Ulid DatabaseId, string Table, ulong SchemaEpoch);

    private sealed record TableMetadata(
        TableColumn[] Columns,
        string[] Names,
        string[] QuotedNames,
        string QuotedTable,
        Dictionary<string, int> ColumnIndex);

    private sealed class TableCacheKeyComparer : IEqualityComparer<TableCacheKey>
    {
        public static TableCacheKeyComparer Instance { get; } = new();

        public bool Equals(TableCacheKey x, TableCacheKey y) =>
            x.DatabaseId == y.DatabaseId && x.SchemaEpoch == y.SchemaEpoch && AsciiIgnoreCaseComparer.Instance.Equals(x.Table, y.Table);

        public int GetHashCode(TableCacheKey obj) =>
            HashCode.Combine(obj.DatabaseId, obj.SchemaEpoch, AsciiIgnoreCaseComparer.Instance.GetHashCode(obj.Table));
    }
}
