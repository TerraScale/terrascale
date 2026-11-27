using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
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

internal readonly union RowValuesOutcome(ExtractedValues, RowRefused, SqlRefusal);

internal enum RowAction
{
    Create,
    Read,
    Update,
    Delete,
}

internal sealed record RowCommand(RowAction Action, string? Table, long? Key, IReadOnlyDictionary<string, JsonElement>? Values);

internal readonly record struct PlannedRow(
    SqlStatement Write,
    RowAction Action,
    RowTablePlan? Plan,
    string? CreateProjected,
    string? UpdateNext,
    int CheckArgOffset,
    int CheckArgCount,
    long UpdateKey);

internal readonly record struct ExtractedValues(
    ulong Mask,
    bool CanUseMask,
    SqlValue[]? MaskedValues,
    List<(int ColumnIndex, string Column, SqlValue Value)>? DynamicValues);

internal readonly record struct SetEntry(int ColumnIndex, string Column, string Expression, bool ExactColumnName);

internal readonly record struct RuleClause(
    CompiledCondition? Compiled,
    ParameterizedCondition? Parameterized,
    string? InvalidMessage,
    bool IsMissing);

internal sealed record TableMetadata(
    TableColumn[] Columns,
    string[] Names,
    string[] QuotedNames,
    string QuotedTable,
    FrozenDictionary<string, int> ColumnIndex,
    ulong NonDeterministicDefaultMask,
    bool HasRowIdColumn,
    bool IsBatchAliasConflict,
    bool CanCoalesceCreate,
    bool CanCoalesceDelete,
    bool CoalesceMetadataChecked);

internal sealed record CreateMaskTemplate(
    int[] Columns,
    int[] SlotByColumn,
    string InsertNames,
    string Projected,
    string BatchRowValues,
    string BatchCteHeader,
    string InsertPrefix,
    string ReturningSuffix,
    string FullSql);

internal sealed record UpdateMaskTemplate(
    int[] Columns,
    string Next,
    string UpdatePrefix,
    string ReturningSuffix,
    string FullSql);

internal sealed class RowTablePlan(
    TableMetadata meta,
    RulesTable? rule,
    bool isEndUser,
    string readableColumns,
    FrozenSet<string> writableSet,
    FrozenSet<string> setCreateNames,
    FrozenSet<string> setUpdateNames,
    SetEntry[] setCreateEntries,
    SetEntry[] setUpdateEntries,
    bool createNeedsUlid,
    bool updateNeedsUlid,
    RuleClause readClause,
    RuleClause deleteClause,
    RuleClause createClause,
    RuleClause updateClause,
    bool validateInvalid,
    CompiledCondition[] validateConditions,
    ParameterizedCondition? createAndValidateParameterized,
    ParameterizedCondition? updateAndValidateParameterized,
    string? readSql,
    string? deleteSql)
{
    private const int MaxMaskTemplates = 256;

    private const int MaxBatchTemplates = 64;

    private readonly ConcurrentDictionary<ulong, CreateMaskTemplate> _createTemplates = new();

    private readonly ConcurrentDictionary<ulong, UpdateMaskTemplate> _updateTemplates = new();

    private readonly ConcurrentDictionary<(ulong Mask, int Count), string> _batchCreateTemplates = new();

    private readonly ConcurrentDictionary<int, string> _batchDeleteTemplates = new();

    private int _createTemplateCount;

    private int _updateTemplateCount;

    private int _batchCreateTemplateCount;

    private int _batchDeleteTemplateCount;

    public TableMetadata Meta => meta;

    public bool CanCoalesceCreate => meta.CanCoalesceCreate;

    public bool CanCoalesceDelete => meta.CanCoalesceDelete;

    public RulesTable? Rule => rule;

    public bool IsEndUser => isEndUser;

    public string ReadableColumns => readableColumns;

    public FrozenSet<string> WritableSet => writableSet;

    public FrozenSet<string> SetCreateNames => setCreateNames;

    public FrozenSet<string> SetUpdateNames => setUpdateNames;

    public SetEntry[] SetCreateEntries => setCreateEntries;

    public SetEntry[] SetUpdateEntries => setUpdateEntries;

    public bool CreateNeedsUlid => createNeedsUlid;

    public bool UpdateNeedsUlid => updateNeedsUlid;

    public RuleClause ReadClause => readClause;

    public RuleClause DeleteClause => deleteClause;

    public RuleClause CreateClause => createClause;

    public RuleClause UpdateClause => updateClause;

    public bool ValidateInvalid => validateInvalid;

    public CompiledCondition[] ValidateConditions => validateConditions;

    public ParameterizedCondition? CreateAndValidateParameterized => createAndValidateParameterized;

    public ParameterizedCondition? UpdateAndValidateParameterized => updateAndValidateParameterized;

    public string? ReadSql => readSql;

    public string? DeleteSql => deleteSql;

    public CreateMaskTemplate GetOrAddCreateTemplate(ulong mask)
    {
        if (_createTemplates.TryGetValue(mask, out var cached))
        {
            return cached;
        }

        var built = BuildCreateMaskTemplate(mask);
        if (Volatile.Read(ref _createTemplateCount) < MaxMaskTemplates && _createTemplates.TryAdd(mask, built))
        {
            Interlocked.Increment(ref _createTemplateCount);
        }

        return built;
    }

    public UpdateMaskTemplate GetOrAddUpdateTemplate(ulong mask)
    {
        if (_updateTemplates.TryGetValue(mask, out var cached))
        {
            return cached;
        }

        var built = BuildUpdateMaskTemplate(mask);
        if (Volatile.Read(ref _updateTemplateCount) < MaxMaskTemplates && _updateTemplates.TryAdd(mask, built))
        {
            Interlocked.Increment(ref _updateTemplateCount);
        }

        return built;
    }

    public string GetOrAddBatchCreateSql(ulong mask, int count, CreateMaskTemplate template)
    {
        var key = (mask, count);
        if (_batchCreateTemplates.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var built = BuildBatchCreateSql(template, count, isEndUser ? createAndValidateParameterized?.Sql : null);
        if (Volatile.Read(ref _batchCreateTemplateCount) < MaxBatchTemplates && _batchCreateTemplates.TryAdd(key, built))
        {
            Interlocked.Increment(ref _batchCreateTemplateCount);
        }

        return built;
    }

    public string BuildBatchCreateSql(CreateMaskTemplate template, int count, string? conditionSql)
    {
        if (conditionSql is null)
        {
            var plain = new StringBuilder((template.Columns.Length * 3 + 4) * count + 64);
            plain.Append("INSERT INTO ");
            plain.Append(meta.QuotedTable);
            plain.Append(" (");
            plain.Append(template.InsertNames);
            plain.Append(") VALUES ");
            for (var r = 0; r < count; r++)
            {
                if (r > 0)
                {
                    plain.Append(", ");
                }

                plain.Append('(');
                for (var c = 0; c < template.Columns.Length; c++)
                {
                    if (c > 0)
                    {
                        plain.Append(", ");
                    }

                    plain.Append('?');
                }

                plain.Append(')');
            }

            plain.Append(" RETURNING rowid, ");
            plain.Append(readableColumns);
            return plain.ToString();
        }

        var sb = new StringBuilder(template.BatchCteHeader.Length + (template.BatchRowValues.Length + 2) * count + conditionSql.Length + 128);
        sb.Append(template.BatchCteHeader);
        for (var r = 0; r < count; r++)
        {
            if (r > 0)
            {
                sb.Append(", ");
            }

            sb.Append(template.BatchRowValues);
        }

        sb.Append(") INSERT INTO ");
        sb.Append(meta.QuotedTable);
        sb.Append(" (");
        sb.Append(template.InsertNames);
        sb.Append(") SELECT ");
        sb.Append(template.InsertNames);
        sb.Append(" FROM _b WHERE NOT EXISTS (SELECT 1 FROM _b AS new WHERE NOT (");
        sb.Append(conditionSql);
        sb.Append(")) RETURNING rowid, ");
        sb.Append(readableColumns);
        return sb.ToString();
    }

    public string GetOrAddBatchDeleteSql(int count)
    {
        if (_batchDeleteTemplates.TryGetValue(count, out var cached))
        {
            return cached;
        }

        var built = BuildBatchDeleteSql(count, isEndUser ? deleteClause.Parameterized?.Sql : null);
        if (Volatile.Read(ref _batchDeleteTemplateCount) < MaxBatchTemplates && _batchDeleteTemplates.TryAdd(count, built))
        {
            Interlocked.Increment(ref _batchDeleteTemplateCount);
        }

        return built;
    }

    public string BuildBatchDeleteSql(int count, string? conditionSql)
    {
        var sb = new StringBuilder(count * 6 + (conditionSql?.Length ?? 0) + 128);
        sb.Append("DELETE FROM ");
        sb.Append(meta.QuotedTable);
        sb.Append(" WHERE rowid IN (");
        AppendPlaceholders(sb, count);
        sb.Append(") AND (SELECT count(*) FROM ");
        sb.Append(meta.QuotedTable);
        sb.Append(" WHERE rowid IN (");
        AppendPlaceholders(sb, count);
        if (conditionSql is not null)
        {
            sb.Append(") AND ");
            sb.Append(conditionSql);
        }
        else
        {
            sb.Append(')');
        }

        sb.Append(") = ");
        sb.Append(count.ToString(CultureInfo.InvariantCulture));
        sb.Append(" RETURNING rowid");
        return sb.ToString();
    }

    public string BuildBatchDeleteDiagnosisSql(int count, string? conditionSql)
    {
        var sb = new StringBuilder(count * 3 + (conditionSql?.Length ?? 0) + 64);
        sb.Append("SELECT rowid FROM ");
        sb.Append(meta.QuotedTable);
        sb.Append(" WHERE rowid IN (");
        AppendPlaceholders(sb, count);
        if (conditionSql is not null)
        {
            sb.Append(") AND ");
            sb.Append(conditionSql);
        }
        else
        {
            sb.Append(')');
        }

        return sb.ToString();
    }

    private static void AppendPlaceholders(StringBuilder sb, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append('?');
        }
    }

    private CreateMaskTemplate BuildCreateMaskTemplate(ulong mask)
    {
        var tableColumns = meta.Columns;
        var quotedNames = meta.QuotedNames;
        var boundCount = BitOperations.PopCount(mask);
        var columns = new int[boundCount];
        var slotByColumn = new int[tableColumns.Length];
        Array.Fill(slotByColumn, -1);

        var namesBuilder = new StringBuilder(boundCount * 12);
        var projectedBuilder = new StringBuilder(tableColumns.Length * 16);
        var cteHeaderBuilder = new StringBuilder(tableColumns.Length * 12 + 24);
        var batchRowBuilder = new StringBuilder(tableColumns.Length * 8 + 4);
        cteHeaderBuilder.Append("WITH _b(");
        batchRowBuilder.Append('(');

        var nextSlot = 0;
        for (var c = 0; c < tableColumns.Length; c++)
        {
            if (c > 0)
            {
                projectedBuilder.Append(", ");
                cteHeaderBuilder.Append(", ");
                batchRowBuilder.Append(", ");
            }

            cteHeaderBuilder.Append(quotedNames[c]);
            if ((mask & (1UL << c)) != 0)
            {
                if (nextSlot > 0)
                {
                    namesBuilder.Append(", ");
                }

                namesBuilder.Append(quotedNames[c]);
                columns[nextSlot] = c;
                slotByColumn[c] = nextSlot;
                nextSlot++;

                projectedBuilder.Append("? AS ");
                projectedBuilder.Append(quotedNames[c]);
                batchRowBuilder.Append('?');
            }
            else if (tableColumns[c].DefaultSql is { Length: > 0 } defaultSql)
            {
                projectedBuilder.Append('(');
                projectedBuilder.Append(defaultSql);
                projectedBuilder.Append(") AS ");
                projectedBuilder.Append(quotedNames[c]);
                batchRowBuilder.Append('(');
                batchRowBuilder.Append(defaultSql);
                batchRowBuilder.Append(')');
            }
            else
            {
                projectedBuilder.Append("NULL AS ");
                projectedBuilder.Append(quotedNames[c]);
                batchRowBuilder.Append("NULL");
            }
        }

        cteHeaderBuilder.Append(") AS (VALUES ");
        batchRowBuilder.Append(')');

        var insertNames = namesBuilder.ToString();
        var projected = projectedBuilder.ToString();
        var insertPrefix = string.Concat("INSERT INTO ", meta.QuotedTable, " (", insertNames, ") SELECT ", insertNames, " FROM (SELECT ", projected, ") AS new WHERE ");
        var returningSuffix = string.Concat(" RETURNING rowid, ", readableColumns);
        var conditionSql = isEndUser ? createAndValidateParameterized?.Sql ?? "1" : "1";
        var fullSql = string.Concat(insertPrefix, conditionSql, returningSuffix);

        return new CreateMaskTemplate(
            columns,
            slotByColumn,
            insertNames,
            projected,
            batchRowBuilder.ToString(),
            cteHeaderBuilder.ToString(),
            insertPrefix,
            returningSuffix,
            fullSql);
    }

    private UpdateMaskTemplate BuildUpdateMaskTemplate(ulong mask)
    {
        var quotedNames = meta.QuotedNames;
        var table = meta.QuotedTable;
        var boundCount = BitOperations.PopCount(mask);
        var columns = new int[boundCount];
        var assignmentsBuilder = new StringBuilder(boundCount * 16);
        var nextBuilder = new StringBuilder(quotedNames.Length * 24);
        var nextSlot = 0;

        for (var c = 0; c < quotedNames.Length; c++)
        {
            if (c > 0)
            {
                nextBuilder.Append(", ");
            }

            if ((mask & (1UL << c)) != 0)
            {
                if (nextSlot > 0)
                {
                    assignmentsBuilder.Append(", ");
                }

                assignmentsBuilder.Append(quotedNames[c]);
                assignmentsBuilder.Append(" = ?");
                columns[nextSlot++] = c;

                nextBuilder.Append("? AS ");
                nextBuilder.Append(quotedNames[c]);
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
        var updatePrefix = string.Concat("UPDATE ", table, " SET ", assignmentsBuilder.ToString(), " WHERE rowid = ? AND EXISTS (SELECT 1 FROM (SELECT ", next, ") AS new WHERE ");
        var returningSuffix = string.Concat(") RETURNING rowid, ", readableColumns);
        var conditionSql = isEndUser ? updateAndValidateParameterized?.Sql ?? "1" : "1";
        var fullSql = string.Concat(updatePrefix, conditionSql, returningSuffix);
        return new UpdateMaskTemplate(columns, next, updatePrefix, returningSuffix, fullSql);
    }
}

internal sealed class RowsService(DatabaseAccess access, RulesStore rules, IClusterCoordinator coordinator, SqlRunner runner, TimeProvider time)
{
    public const string Rows = "/v1/projects/{project}/databases/{database}/tables/{table}/rows";

    public const string Row = Rows + "/{key}";

    public const string Batch = "/v1/projects/{project}/databases/{database}/rows:batch";

    public const int MaxOperations = 100;

    private const int MaxTableCacheEntries = 4096;

    private const int MaxCoalescedParameters = 900;

    private static readonly RulesSet EmptyRules = new(FrozenDictionary<string, RulesTable>.Empty);

    private readonly ConcurrentDictionary<TableCacheKey, TableMetadata> _tableCache = new(TableCacheKeyComparer.Instance);

    private readonly ConcurrentDictionary<RowTablePlanKey, RowTablePlan> _planCache = new(RowTablePlanKeyComparer.Instance);

    private int _tableCacheCount;

    private int _planCacheCount;

    public async ValueTask<RowsOutcome> RunAsync(
        Caller caller, string? project, string? database, IReadOnlyList<RowCommand> commands, bool batch, CancellationToken cancellationToken)
    {
        var outcome = await access.DataAsync(caller, project, database, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase target => await access.OpenAsync(target.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => commands.Count == 1
                    ? await ExecuteSingleAsync(target, session, commands[0], batch, cancellationToken)
                    : await ExecuteAsync(target, session, commands, batch, cancellationToken),
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<RowsOutcome> RunSingleAsync(
        Caller caller, string? project, string? database, RowCommand command, CancellationToken cancellationToken)
    {
        var outcome = await access.DataAsync(caller, project, database, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase target => await access.OpenAsync(target.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => await ExecuteSingleAsync(target, session, command, batch: false, cancellationToken),
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

    private async ValueTask<RowsOutcome> ExecuteSingleAsync(
        ResolvedDatabase target, ISqlDatabaseSession session, RowCommand command, bool batch, CancellationToken cancellationToken)
    {
        var writes = command.Action != RowAction.Read;
        if (writes && !target.CanWrite)
        {
            return SqlRefusal.ForbiddenScope(0);
        }

        if (command.Table is not { Length: > 0 } table)
        {
            return new InvalidInput("table names the table", "invalid_table");
        }

        var (set, rulesVersion) = await LoadRulesAsync(target, cancellationToken);
        var plan = await GetTablePlanAsync(session, table, set, rulesVersion, target.EndUser is not null, cancellationToken);
        if (plan is null)
        {
            return new ResourceNotFound(string.Concat("the database has no table ", table), "unknown_table");
        }

        var now = time.GetUtcNow();
        var renderTime = now.ToUnixTimeMilliseconds();
        var built = Build(command, table, plan, target.EndUser, now, renderTime, new Position(0, batch));
        return built switch
        {
            PlannedRow planned => await ExecuteSinglePlannedAsync(target, session, command, planned, batch, writes, renderTime, cancellationToken),
            RowRefused refused => refused,
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<RowsOutcome> ExecuteSinglePlannedAsync(
        ResolvedDatabase target,
        ISqlDatabaseSession session,
        RowCommand command,
        PlannedRow planned,
        bool batch,
        bool writes,
        long renderTime,
        CancellationToken cancellationToken)
    {
        var request = new SqlStatementRequest(target.Database.Id, [planned.Write], writes ? SqlAccess.Write : SqlAccess.Read,
            target.Database.DefaultConsistency, CausalBookmark.Empty);
        if (writes)
        {
            var writtenOutcome = await coordinator.ExecuteWriteAsync(session, request, cancellationToken);
            return writtenOutcome switch
            {
                SqlWriteResult written => await SingleAnswerOrValidateAsync(session, target, written.ResultSets, command, planned, batch, renderTime, cancellationToken),
                SqlError error => new RowsOutcome(SqlRefusal.From(error)),
                SerializationConflict conflict => new RowsOutcome(SqlRefusal.From(conflict)),
                SchemaMismatch mismatch => new RowsOutcome(SqlRefusal.From(mismatch)),
                ClusterUnavailable unavailable => new RowsOutcome(SqlRefusal.From(unavailable)),
                ClusterTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
                StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
            };
        }

        var readOutcome = await session.ExecuteReadAsync(request, cancellationToken);
        return readOutcome switch
        {
            SqlReadResult read => SingleAnswer(target, read.ResultSets, command, batch, wrote: false),
            SqlError error => new RowsOutcome(SqlRefusal.From(error)),
            StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
            StorageTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
        };
    }

    private async ValueTask<RowsOutcome> SingleAnswerOrValidateAsync(
        ISqlDatabaseSession session,
        ResolvedDatabase target,
        IReadOnlyList<SqlResultSet> results,
        RowCommand command,
        PlannedRow planned,
        bool batch,
        long renderTime,
        CancellationToken cancellationToken)
    {
        if (results.Count > 0 && results[0].Rows.Count == 0
            && BuildCheckStatement(planned, target.EndUser, command.Table, renderTime) is { } check)
        {
            var read = await session.ExecuteReadAsync(
                new SqlStatementRequest(target.Database.Id, [check], SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty),
                cancellationToken);
            var validation = read switch
            {
                SqlReadResult answered when answered.ResultSets.Count > 0 => CheckSingleResult(answered.ResultSets[0], command, 0, batch),
                SqlReadResult => null,
                SqlError error => new RowsOutcome(SqlRefusal.From(error)),
                StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
                StorageTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
            };
            if (validation is { } refusedValidation)
            {
                return refusedValidation;
            }
        }

        return SingleAnswer(target, results, command, batch, wrote: true);
    }

    private RowsOutcome SingleAnswer(ResolvedDatabase target, IReadOnlyList<SqlResultSet> results, RowCommand command, bool batch, bool wrote)
    {
        runner.Meter(target, results, wrote, target.Database.DefaultConsistency);
        if (results.Count == 0)
        {
            return new RowsOutcome([]);
        }

        var result = results[0];
        if (result.Rows.Count == 0)
        {
            return new RowRefused(
                command.Action == RowAction.Create ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound,
                command.Action == RowAction.Create ? "rule_denied" : "row_not_found",
                command.Action == RowAction.Create ? "the rules refused the row" : "the row does not exist or the rules hide it",
                new RowsErrorDetails(Table: command.Table, Index: batch ? 0 : null));
        }

        return new RowsOutcome([BuildRowAnswer(command.Action, result, result.Rows[0])]);
    }

    private async ValueTask<RowsOutcome> ExecuteAsync(
        ResolvedDatabase target, ISqlDatabaseSession session, IReadOnlyList<RowCommand> commands, bool batch, CancellationToken cancellationToken)
    {
        var writes = HasWrites(commands);
        if (writes && !target.CanWrite)
        {
            return SqlRefusal.ForbiddenScope(0);
        }

        var (set, rulesVersion) = await LoadRulesAsync(target, cancellationToken);
        var now = time.GetUtcNow();
        var renderTime = now.ToUnixTimeMilliseconds();

        if (writes && batch && commands.Count > 1 && TryClassifyHomogeneousBatch(commands, out var batchAction, out var batchTable))
        {
            var batchPlan = await GetTablePlanAsync(session, batchTable, set, rulesVersion, target.EndUser is not null, cancellationToken);
            if (batchPlan is null)
            {
                return new ResourceNotFound(string.Concat("the database has no table ", batchTable), "unknown_table");
            }

            if (batchAction == RowAction.Create)
            {
                if (await TryExecuteCoalescedCreateBatchAsync(target, session, commands, batchTable, batchPlan, now, renderTime, cancellationToken) is { } coalescedCreate)
                {
                    return coalescedCreate;
                }
            }
            else if (batchAction == RowAction.Delete)
            {
                if (await TryExecuteCoalescedDeleteBatchAsync(target, session, commands, batchTable, batchPlan, renderTime, cancellationToken) is { } coalescedDelete)
                {
                    return coalescedDelete;
                }
            }
        }

        var planned = new PlannedRow[commands.Count];
        string? lastTable = null;
        RowTablePlan? lastPlan = null;
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index].Table is not { Length: > 0 } table)
            {
                return new InvalidInput("table names the table", "invalid_table");
            }

            RowTablePlan? plan;
            if (lastPlan is not null && lastTable is not null && AsciiIgnoreCaseComparer.Instance.Equals(lastTable, table))
            {
                plan = lastPlan;
            }
            else
            {
                plan = await GetTablePlanAsync(session, table, set, rulesVersion, target.EndUser is not null, cancellationToken);
                if (plan is null)
                {
                    return new ResourceNotFound(string.Concat("the database has no table ", table), "unknown_table");
                }

                lastTable = table;
                lastPlan = plan;
            }

            var built = Build(commands[index], table, plan, target.EndUser, now, renderTime, new Position(index, batch));
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

        if (writes && batch && commands.Count > 1)
        {
            return await ExecuteBatchWriteAsync(target, session, commands, planned, renderTime, cancellationToken);
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
                SqlWriteResult written => await AnswersOrValidateAsync(session, target, written.ResultSets, commands, planned, batch, wrote: true, renderTime, cancellationToken),
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

    private static bool TryClassifyHomogeneousBatch(IReadOnlyList<RowCommand> commands, out RowAction action, out string table)
    {
        action = commands[0].Action;
        if (action is not (RowAction.Create or RowAction.Delete) || commands[0].Table is not { Length: > 0 } firstTable)
        {
            table = "";
            return false;
        }

        for (var i = 1; i < commands.Count; i++)
        {
            var command = commands[i];
            if (command.Action != action || command.Table is not { Length: > 0 } nextTable || !AsciiIgnoreCaseComparer.Instance.Equals(firstTable, nextTable))
            {
                table = "";
                return false;
            }
        }

        table = firstTable;
        return true;
    }

    private async ValueTask<RowsOutcome?> TryExecuteCoalescedCreateBatchAsync(
        ResolvedDatabase target,
        ISqlDatabaseSession session,
        IReadOnlyList<RowCommand> commands,
        string table,
        RowTablePlan plan,
        DateTimeOffset now,
        long renderTime,
        CancellationToken cancellationToken)
    {
        var meta = plan.Meta;
        if (!plan.CanCoalesceCreate || meta.Columns.Length > 64 || meta.IsBatchAliasConflict)
        {
            return null;
        }

        var auth = target.EndUser;
        if (auth is not null && plan.Rule is null)
        {
            return new RowsOutcome(SqlRefusal.RuleDenied(string.Concat("the rules do not cover ", table), 0));
        }

        var firstExtracted = ExtractValues(commands[0], table, plan, auth, now, renderTime, new Position(0, Batch: true));
        var (firstValues, firstFailure) = UnpackExtractedForBatch(firstExtracted);
        if (firstFailure is not null)
        {
            return firstFailure;
        }

        if (!firstValues.CanUseMask || firstValues.MaskedValues is not { } firstMasked || firstValues.Mask == 0)
        {
            return null;
        }

        var mask = firstValues.Mask;
        if ((meta.NonDeterministicDefaultMask & ~mask) != 0)
        {
            return null;
        }

        if (auth is not null)
        {
            if (CheckCreateRules(plan, 0) is { } ruleFailure)
            {
                return new RowsOutcome(ruleFailure);
            }
        }

        var resolvedCondition = ResolveCombinedCondition(
            plan.CreateAndValidateParameterized, plan.CreateClause.Compiled, plan.ValidateConditions, auth, newPrefix: "new.", rowPrefix: null);
        var ruleParamCount = resolvedCondition?.Parameters.Length ?? 0;
        var template = plan.GetOrAddCreateTemplate(mask);
        var boundCount = template.Columns.Length;
        var totalParams = commands.Count * boundCount + ruleParamCount;
        if (totalParams > MaxCoalescedParameters)
        {
            return null;
        }

        var args = new SqlValue[totalParams];
        for (var i = 0; i < boundCount; i++)
        {
            args[i] = firstMasked[template.Columns[i]];
        }

        var slotByColumn = template.SlotByColumn;
        for (var rowIdx = 1; rowIdx < commands.Count; rowIdx++)
        {
            var rowOffset = rowIdx * boundCount;
            var matched = FillCoalescedCreateRow(commands[rowIdx], table, plan, auth, now, renderTime, rowIdx, mask, slotByColumn, args.AsSpan(rowOffset, boundCount), out var rowFailure);
            if (rowFailure is not null)
            {
                return rowFailure;
            }

            if (!matched)
            {
                return null;
            }
        }

        if (auth is not null && resolvedCondition is not null && ruleParamCount > 0)
        {
            RulesCompiler.BindParameters(resolvedCondition.Parameters, auth, renderTime, args.AsSpan(commands.Count * boundCount, ruleParamCount));
        }

        var sql = auth is null || ReferenceEquals(resolvedCondition, plan.CreateAndValidateParameterized)
            ? plan.GetOrAddBatchCreateSql(mask, commands.Count, template)
            : plan.BuildBatchCreateSql(template, commands.Count, resolvedCondition?.Sql);

        var request = new SqlStatementRequest(target.Database.Id, [new SqlStatement(sql, args, SqlBuild.NoNamed)], SqlAccess.Write,
            target.Database.DefaultConsistency, CausalBookmark.Empty);
        var writeOutcome = await coordinator.ExecuteWriteAsync(session, request, cancellationToken);
        var (written, writeFailure) = UnpackCoalescedWrite(writeOutcome);
        if (writeFailure is not null || written is null)
        {
            return writeFailure;
        }

        return await FinishCoalescedCreateBatchAsync(session, target, written, commands, table, plan, template, args, boundCount, renderTime, cancellationToken);
    }

    private static (SqlWriteResult? Written, RowsOutcome? Failure) UnpackCoalescedWrite(ClusterWriteOutcome outcome) => outcome switch
    {
        SqlWriteResult written => (written, null),
        SqlError error => (null, new RowsOutcome(SqlRefusal.From(error))),
        SerializationConflict conflict => (null, new RowsOutcome(SqlRefusal.From(conflict))),
        SchemaMismatch mismatch => (null, new RowsOutcome(SqlRefusal.From(mismatch))),
        ClusterUnavailable unavailable => (null, new RowsOutcome(SqlRefusal.From(unavailable))),
        ClusterTimeout timeout => (null, new RowsOutcome(SqlRefusal.From(timeout))),
        StorageBusy busy => (null, new RowsOutcome(SqlRefusal.From(busy))),
    };

    private static (ExtractedValues Values, RowsOutcome? Failure) UnpackExtractedForBatch(RowValuesOutcome extracted) => extracted switch
    {
        ExtractedValues ok => (ok, null),
        RowRefused refused => (default, new RowsOutcome(refused)),
        SqlRefusal refusal => (default, new RowsOutcome(refusal)),
    };

    private static bool FillCoalescedCreateRow(
        RowCommand command,
        string table,
        RowTablePlan plan,
        RulesAuth? auth,
        DateTimeOffset now,
        long renderTime,
        int index,
        ulong expectedMask,
        int[] slotByColumn,
        Span<SqlValue> destination,
        out RowsOutcome? failure)
    {
        failure = null;
        var meta = plan.Meta;
        var position = new Position(index, Batch: true);
        ulong rowMask = 0;

        if (command.Values is { Count: > 0 } rawValues)
        {
            foreach (var (column, element) in rawValues)
            {
                if (!meta.ColumnIndex.TryGetValue(column, out var columnIdx))
                {
                    failure = new RowsOutcome(new RowRefused(StatusCodes.Status400BadRequest, "unknown_column",
                        string.Concat("the table ", table, " has no column ", column), position.Details(table, column)));
                    return false;
                }

                var known = meta.Names[columnIdx];
                if (SqlWire.Value(element) is not { } value)
                {
                    failure = new RowsOutcome(new RowRefused(StatusCodes.Status400BadRequest, "invalid_request",
                        string.Concat(known, " is not a SQL value"), position.Details(table, known)));
                    return false;
                }

                if (auth is not null && (!plan.WritableSet.Contains(known) || plan.SetCreateNames.Contains(known)))
                {
                    failure = new RowsOutcome(new RowRefused(StatusCodes.Status403Forbidden, "column_not_writable",
                        string.Concat(known, " is not writable"), position.Details(table, known)));
                    return false;
                }

                var bit = 1UL << columnIdx;
                if ((rowMask & bit) != 0)
                {
                    return false;
                }

                var slot = slotByColumn[columnIdx];
                if (slot < 0)
                {
                    return false;
                }

                rowMask |= bit;
                destination[slot] = value;
            }
        }

        if (auth is not null && plan.SetCreateEntries.Length > 0)
        {
            var ulid = plan.CreateNeedsUlid ? Ulid.NewUlid(now) : default;
            var entries = plan.SetCreateEntries;
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.ColumnIndex < 0)
                {
                    return false;
                }

                var slot = slotByColumn[entry.ColumnIndex];
                if (slot < 0)
                {
                    return false;
                }

                rowMask |= 1UL << entry.ColumnIndex;
                destination[slot] = RulesCompiler.SetValue(entry.Expression, auth, renderTime, ulid);
            }
        }

        if (rowMask == 0)
        {
            failure = new RowsOutcome(SqlRefusal.Invalid("row names at least one column", index));
            return false;
        }

        return rowMask == expectedMask;
    }

    private async ValueTask<RowsOutcome> FinishCoalescedCreateBatchAsync(
        ISqlDatabaseSession session,
        ResolvedDatabase target,
        SqlWriteResult written,
        IReadOnlyList<RowCommand> commands,
        string table,
        RowTablePlan plan,
        CreateMaskTemplate template,
        SqlValue[] args,
        int boundCount,
        long renderTime,
        CancellationToken cancellationToken)
    {
        if (written.ResultSets.Count > 0 && written.ResultSets[0].Rows.Count == commands.Count)
        {
            runner.Meter(target, written.ResultSets, wrote: true, target.Database.DefaultConsistency);
            var result = written.ResultSets[0];
            var rows = result.Rows;
            var answers = new RowAnswer[rows.Count];
            for (var i = 0; i < rows.Count; i++)
            {
                answers[i] = BuildRowAnswer(RowAction.Create, result, rows[i]);
            }

            return new RowsOutcome(answers);
        }

        if (target.EndUser is { } auth && plan.CreateClause.Compiled is { } createCompiled)
        {
            var checkSql = BuildCoalescedCreateCheckSql(createCompiled, plan.ValidateConditions, template.Projected, auth, renderTime);
            var checkStatements = new SqlStatement[commands.Count];
            for (var i = 0; i < commands.Count; i++)
            {
                var rowArgs = new SqlValue[boundCount];
                Array.Copy(args, i * boundCount, rowArgs, 0, boundCount);
                checkStatements[i] = new SqlStatement(checkSql, rowArgs, SqlBuild.NoNamed);
            }

            var read = await session.ExecuteReadAsync(
                new SqlStatementRequest(target.Database.Id, checkStatements, SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty),
                cancellationToken);
            return read switch
            {
                SqlReadResult answered => DiagnoseCoalescedCreateResults(answered.ResultSets, table, commands.Count),
                SqlError error => new RowsOutcome(SqlRefusal.From(error)),
                StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
                StorageTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
            };
        }

        return new RowRefused(StatusCodes.Status403Forbidden, "rule_denied", "the rules refused the row", new RowsErrorDetails(Table: table, Index: 0));
    }

    private static string BuildCoalescedCreateCheckSql(
        CompiledCondition createCompiled, CompiledCondition[] validateConditions, string projected, RulesAuth auth, long renderTime)
    {
        var sb = new StringBuilder(projected.Length + 128);
        sb.Append("SELECT ");
        sb.Append(RulesCompiler.Render(createCompiled, auth, renderTime, newPrefix: "new."));
        for (var i = 0; i < validateConditions.Length; i++)
        {
            sb.Append(", ");
            sb.Append(RulesCompiler.Render(validateConditions[i], auth, renderTime, newPrefix: "new."));
        }

        sb.Append(" FROM (SELECT ");
        sb.Append(projected);
        sb.Append(") AS new");
        return sb.ToString();
    }

    private static RowsOutcome DiagnoseCoalescedCreateResults(IReadOnlyList<SqlResultSet> results, string table, int count)
    {
        var limit = Math.Min(count, results.Count);
        for (var index = 0; index < limit; index++)
        {
            if (results[index].Rows is not [var row, ..])
            {
                return new RowRefused(StatusCodes.Status403Forbidden, "rule_denied", "the rules refused the row",
                    new RowsErrorDetails(Table: table, Index: index));
            }

            if (!IsTrue(row[0]))
            {
                return new RowRefused(StatusCodes.Status403Forbidden, "rule_denied", "the rules refused the row",
                    new RowsErrorDetails(Table: table, Index: index));
            }

            for (var clause = 1; clause < row.Count; clause++)
            {
                if (!IsTrue(row[clause]))
                {
                    return new RowRefused(StatusCodes.Status400BadRequest, "validation_failed",
                        string.Concat("validate[", (clause - 1).ToString(CultureInfo.InvariantCulture), "] is false for the written row"),
                        new RowsErrorDetails(Table: table, Index: index, Clause: clause - 1));
                }
            }
        }

        return new RowRefused(StatusCodes.Status403Forbidden, "rule_denied", "the rules refused the row", new RowsErrorDetails(Table: table, Index: 0));
    }

    private async ValueTask<RowsOutcome?> TryExecuteCoalescedDeleteBatchAsync(
        ResolvedDatabase target,
        ISqlDatabaseSession session,
        IReadOnlyList<RowCommand> commands,
        string table,
        RowTablePlan plan,
        long renderTime,
        CancellationToken cancellationToken)
    {
        if (!plan.CanCoalesceDelete || plan.Meta.HasRowIdColumn)
        {
            return null;
        }

        for (var i = 0; i < commands.Count; i++)
        {
            if (commands[i].Values is { Count: > 0 })
            {
                return null;
            }
        }

        var count = commands.Count;
        var keys = new long[count];
        for (var i = 0; i < count; i++)
        {
            keys[i] = commands[i].Key ?? 0;
        }

        if (!AreKeysDistinct(keys))
        {
            return null;
        }

        var auth = target.EndUser;
        if (auth is not null)
        {
            if (plan.Rule is null)
            {
                return new RowsOutcome(SqlRefusal.RuleDenied(string.Concat("the rules do not cover ", table), 0));
            }

            if (plan.DeleteClause.IsMissing)
            {
                return new RowsOutcome(SqlRefusal.RuleDenied("the rules do not allow this operation", 0));
            }

            if (plan.DeleteClause.InvalidMessage is { } invalidMessage)
            {
                return new RowsOutcome(new SqlRefusal("rules_invalid", invalidMessage, "42501", 0, StatusCodes.Status403Forbidden));
            }
        }

        var resolvedDelete = ResolveCondition(plan.DeleteClause, auth, newPrefix: null, rowPrefix: null);
        var ruleParamCount = resolvedDelete?.Parameters.Length ?? 0;
        var totalParams = count * 2 + ruleParamCount;
        if (totalParams > MaxCoalescedParameters)
        {
            return null;
        }

        var args = new SqlValue[totalParams];
        for (var i = 0; i < count; i++)
        {
            var keyValue = SqlBuild.Integer(keys[i]);
            args[i] = keyValue;
            args[count + i] = keyValue;
        }

        if (auth is not null && resolvedDelete is not null && ruleParamCount > 0)
        {
            RulesCompiler.BindParameters(resolvedDelete.Parameters, auth, renderTime, args.AsSpan(count * 2, ruleParamCount));
        }

        var sql = auth is null || ReferenceEquals(resolvedDelete, plan.DeleteClause.Parameterized)
            ? plan.GetOrAddBatchDeleteSql(count)
            : plan.BuildBatchDeleteSql(count, resolvedDelete?.Sql);

        var request = new SqlStatementRequest(target.Database.Id, [new SqlStatement(sql, args, SqlBuild.NoNamed)], SqlAccess.Write,
            target.Database.DefaultConsistency, CausalBookmark.Empty);
        var writeOutcome = await coordinator.ExecuteWriteAsync(session, request, cancellationToken);
        return writeOutcome switch
        {
            SqlWriteResult written => await FinishCoalescedDeleteBatchAsync(session, target, written, keys, table, plan, resolvedDelete, renderTime, cancellationToken),
            SqlError error => new RowsOutcome(SqlRefusal.From(error)),
            SerializationConflict conflict => new RowsOutcome(SqlRefusal.From(conflict)),
            SchemaMismatch mismatch => new RowsOutcome(SqlRefusal.From(mismatch)),
            ClusterUnavailable unavailable => new RowsOutcome(SqlRefusal.From(unavailable)),
            ClusterTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
            StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
        };
    }

    private async ValueTask<RowsOutcome> FinishCoalescedDeleteBatchAsync(
        ISqlDatabaseSession session,
        ResolvedDatabase target,
        SqlWriteResult written,
        long[] keys,
        string table,
        RowTablePlan plan,
        ParameterizedCondition? resolvedDelete,
        long renderTime,
        CancellationToken cancellationToken)
    {
        if (written.ResultSets.Count > 0 && written.ResultSets[0].Rows.Count == keys.Length)
        {
            runner.Meter(target, written.ResultSets, wrote: true, target.Database.DefaultConsistency);
            var answers = new RowAnswer[keys.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                answers[i] = new RowAnswer(keys[i].ToString(CultureInfo.InvariantCulture), null);
            }

            return new RowsOutcome(answers);
        }

        var ruleParamCount = resolvedDelete?.Parameters.Length ?? 0;
        var checkArgs = new SqlValue[keys.Length + ruleParamCount];
        for (var i = 0; i < keys.Length; i++)
        {
            checkArgs[i] = SqlBuild.Integer(keys[i]);
        }

        if (target.EndUser is { } auth && resolvedDelete is not null && ruleParamCount > 0)
        {
            RulesCompiler.BindParameters(resolvedDelete.Parameters, auth, renderTime, checkArgs.AsSpan(keys.Length, ruleParamCount));
        }

        var checkSql = plan.BuildBatchDeleteDiagnosisSql(keys.Length, resolvedDelete?.Sql);
        var read = await session.ExecuteReadAsync(
            new SqlStatementRequest(target.Database.Id, [new SqlStatement(checkSql, checkArgs, SqlBuild.NoNamed)], SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty),
            cancellationToken);
        return read switch
        {
            SqlReadResult answered => DiagnoseCoalescedDeleteResults(answered.ResultSets, keys, table),
            SqlError error => new RowsOutcome(SqlRefusal.From(error)),
            StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
            StorageTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
        };
    }

    private static RowsOutcome DiagnoseCoalescedDeleteResults(IReadOnlyList<SqlResultSet> results, long[] keys, string table)
    {
        HashSet<long>? present = null;
        if (results.Count > 0)
        {
            var rows = results[0].Rows;
            if (rows.Count > 0)
            {
                present = new HashSet<long>(rows.Count);
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Count > 0 && rows[i][0].TryGetIntegral(out var rowId))
                    {
                        present.Add(rowId);
                    }
                }
            }
        }

        for (var i = 0; i < keys.Length; i++)
        {
            if (present is null || !present.Contains(keys[i]))
            {
                return new RowRefused(StatusCodes.Status404NotFound, "row_not_found", "the row does not exist or the rules hide it",
                    new RowsErrorDetails(Table: table, Index: i));
            }
        }

        return new RowRefused(StatusCodes.Status404NotFound, "row_not_found", "the row does not exist or the rules hide it",
            new RowsErrorDetails(Table: table, Index: 0));
    }

    private static bool AreKeysDistinct(long[] keys)
    {
        if (keys.Length <= 8)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                var current = keys[i];
                for (var j = i + 1; j < keys.Length; j++)
                {
                    if (keys[j] == current)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        var seen = new HashSet<long>(keys.Length);
        for (var i = 0; i < keys.Length; i++)
        {
            if (!seen.Add(keys[i]))
            {
                return false;
            }
        }

        return true;
    }

    private ValueTask<(RulesSet? Set, string Version)> LoadRulesAsync(ResolvedDatabase target, CancellationToken cancellationToken)
    {
        if (target.EndUser is null)
        {
            return ValueTask.FromResult<(RulesSet?, string)>((null, ""));
        }

        var loaded = rules.CurrentAsync(target.Database.Id, cancellationToken);
        if (loaded.IsCompletedSuccessfully)
        {
            return ValueTask.FromResult(MapLoadedRules(loaded.Result));
        }

        return LoadRulesSlowAsync(loaded);
    }

    private static async ValueTask<(RulesSet? Set, string Version)> LoadRulesSlowAsync(ValueTask<StoredRulesLookup> loadedTask) =>
        MapLoadedRules(await loadedTask);

    private static (RulesSet? Set, string Version) MapLoadedRules(StoredRulesLookup lookup) => lookup switch
    {
        StoredRules current => (current.Rules, current.Version),
        ResourceNotFound or BackendUnavailable => (EmptyRules, ""),
    };

    private ValueTask<RowTablePlan?> GetTablePlanAsync(
        ISqlDatabaseSession session, string table, RulesSet? set, string rulesVersion, bool isEndUser, CancellationToken cancellationToken)
    {
        var epoch = session.SchemaEpoch;
        var incarnation = session.SessionIncarnation;
        if (!isEndUser || rulesVersion.Length > 0)
        {
            var planKey = new RowTablePlanKey(session.DatabaseId, incarnation, epoch, table, isEndUser ? rulesVersion : "");
            if (_planCache.TryGetValue(planKey, out var cachedPlan))
            {
                return ValueTask.FromResult<RowTablePlan?>(cachedPlan);
            }

            return LoadTablePlanAsync(session, table, set, isEndUser, epoch, incarnation, planKey, cacheable: true, cancellationToken);
        }

        return LoadTablePlanAsync(session, table, set, isEndUser, epoch, incarnation, default, cacheable: false, cancellationToken);
    }

    private async ValueTask<RowTablePlan?> LoadTablePlanAsync(
        ISqlDatabaseSession session,
        string table,
        RulesSet? set,
        bool isEndUser,
        ulong epoch,
        long incarnation,
        RowTablePlanKey planKey,
        bool cacheable,
        CancellationToken cancellationToken)
    {
        var meta = await GetTableMetadataAsync(session, table, cancellationToken);
        if (meta is null)
        {
            return null;
        }

        var rule = isEndUser ? set?.Table(table) : null;
        var plan = BuildTablePlan(meta, rule, isEndUser);
        if (cacheable && meta.CoalesceMetadataChecked && session.SchemaEpoch == epoch && session.SessionIncarnation == incarnation)
        {
            StoreTablePlan(planKey, plan);
        }

        return plan;
    }

    private static RowTablePlan BuildTablePlan(TableMetadata meta, RulesTable? rule, bool isEndUser)
    {
        var readable = rule?.Columns is { Count: > 0 } chosen ? FormatReadableColumns(chosen) : "*";
        var writableSet = rule?.Writable is { Count: > 0 } writable
            ? writable.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            : FrozenSet<string>.Empty;
        var (setCreateNames, setCreateEntries, createNeedsUlid) = BuildSetEntries(rule?.Set?.Create, meta);
        var (setUpdateNames, setUpdateEntries, updateNeedsUlid) = BuildSetEntries(rule?.Set?.Update, meta);

        var readClause = CompileRuleClause(rule?.Read, isEndUser, RulesOperation.Read, meta.Names, newPrefix: null, rowPrefix: null);
        var deleteClause = CompileRuleClause(rule?.Delete, isEndUser, RulesOperation.Delete, meta.Names, newPrefix: null, rowPrefix: null);
        var createClause = CompileRuleClause(rule?.Create, isEndUser, RulesOperation.Create, meta.Names, newPrefix: "new.", rowPrefix: null);
        var updateClause = CompileRuleClause(rule?.Update, isEndUser, RulesOperation.Update, meta.Names, newPrefix: "new.", rowPrefix: string.Concat(meta.QuotedTable, "."));

        var validateInvalid = false;
        CompiledCondition[] validateConditions = [];
        ParameterizedCondition[] validateParameterized = [];
        if (isEndUser && rule?.Validate is { Count: > 0 } rawValidates)
        {
            var compiledList = new CompiledCondition[rawValidates.Count];
            var paramList = new ParameterizedCondition[rawValidates.Count];
            for (var i = 0; i < rawValidates.Count; i++)
            {
                var compiled = RulesCompiler.Compile(rawValidates[i], meta.Names, RulesOperation.Validate) switch
                {
                    CompiledCondition ready => ready,
                    InvalidInput => null,
                };
                if (compiled is null)
                {
                    validateInvalid = true;
                    break;
                }

                compiledList[i] = compiled;
                paramList[i] = RulesCompiler.CompileParameterized(compiled, newPrefix: "new.");
            }

            if (!validateInvalid)
            {
                validateConditions = compiledList;
                validateParameterized = paramList;
            }
        }

        var createAndValidate = isEndUser && !validateInvalid && createClause.Parameterized is { } createParam
            ? CombineParameterized(createParam, validateParameterized)
            : null;
        var updateAndValidate = isEndUser && !validateInvalid && updateClause.Parameterized is { } updateParam
            ? CombineParameterized(updateParam, validateParameterized)
            : null;

        var readSql = !isEndUser
            ? string.Concat("SELECT rowid, ", readable, " FROM ", meta.QuotedTable, " WHERE rowid = ? AND 1")
            : readClause.Parameterized is { } rp
                ? string.Concat("SELECT rowid, ", readable, " FROM ", meta.QuotedTable, " WHERE rowid = ? AND ", rp.Sql)
                : null;
        var deleteSql = !isEndUser
            ? string.Concat("DELETE FROM ", meta.QuotedTable, " WHERE rowid = ? AND 1 RETURNING rowid")
            : deleteClause.Parameterized is { } dp
                ? string.Concat("DELETE FROM ", meta.QuotedTable, " WHERE rowid = ? AND ", dp.Sql, " RETURNING rowid")
                : null;

        return new RowTablePlan(
            meta,
            rule,
            isEndUser,
            readable,
            writableSet,
            setCreateNames,
            setUpdateNames,
            setCreateEntries,
            setUpdateEntries,
            createNeedsUlid,
            updateNeedsUlid,
            readClause,
            deleteClause,
            createClause,
            updateClause,
            validateInvalid,
            validateConditions,
            createAndValidate,
            updateAndValidate,
            readSql,
            deleteSql);
    }

    private static (FrozenSet<string> Names, SetEntry[] Entries, bool NeedsUlid) BuildSetEntries(
        IReadOnlyDictionary<string, string>? assigned, TableMetadata meta)
    {
        if (assigned is null || assigned.Count == 0)
        {
            return (FrozenSet<string>.Empty, [], false);
        }

        var deduped = new List<SetEntry>(assigned.Count);
        var needsUlid = false;
        foreach (var (column, expression) in assigned)
        {
            for (var i = deduped.Count - 1; i >= 0; i--)
            {
                if (string.Equals(deduped[i].Column, column, StringComparison.OrdinalIgnoreCase))
                {
                    deduped.RemoveAt(i);
                }
            }

            var columnIdx = meta.ColumnIndex.TryGetValue(column, out var found) ? found : -1;
            var exact = columnIdx >= 0 && string.Equals(column, meta.Names[columnIdx], StringComparison.Ordinal);
            if (string.Equals(expression, "request.ulid", StringComparison.Ordinal))
            {
                needsUlid = true;
            }

            deduped.Add(new SetEntry(columnIdx, column, expression, exact));
        }

        var names = new HashSet<string>(deduped.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < deduped.Count; i++)
        {
            names.Add(deduped[i].Column);
        }

        return (names.ToFrozenSet(StringComparer.OrdinalIgnoreCase), [.. deduped], needsUlid);
    }

    private static RuleClause CompileRuleClause(
        string? condition, bool isEndUser, RulesOperation operation, IReadOnlyList<string> columns, string? newPrefix, string? rowPrefix)
    {
        if (!isEndUser)
        {
            return new RuleClause(null, null, null, IsMissing: false);
        }

        if (condition is null)
        {
            return new RuleClause(null, null, null, IsMissing: true);
        }

        return RulesCompiler.Compile(condition, columns, operation) switch
        {
            CompiledCondition compiled => new RuleClause(compiled, RulesCompiler.CompileParameterized(compiled, newPrefix, rowPrefix), null, IsMissing: false),
            InvalidInput refused => new RuleClause(null, null, refused.Message, IsMissing: false),
        };
    }

    private static ParameterizedCondition CombineParameterized(ParameterizedCondition primary, ParameterizedCondition[] validates)
    {
        if (validates.Length == 0)
        {
            return primary;
        }

        var sqlLength = primary.Sql.Length;
        var paramCount = primary.Parameters.Length;
        var hasClaimList = primary.HasClaimList;
        for (var i = 0; i < validates.Length; i++)
        {
            sqlLength += 5 + validates[i].Sql.Length;
            paramCount += validates[i].Parameters.Length;
            hasClaimList |= validates[i].HasClaimList;
        }

        var sb = new StringBuilder(sqlLength);
        sb.Append(primary.Sql);
        var parameters = paramCount == 0 ? [] : new ConditionParameter[paramCount];
        var offset = 0;
        if (primary.Parameters.Length > 0)
        {
            Array.Copy(primary.Parameters, 0, parameters, 0, primary.Parameters.Length);
            offset = primary.Parameters.Length;
        }

        for (var i = 0; i < validates.Length; i++)
        {
            sb.Append(" AND ");
            sb.Append(validates[i].Sql);
            var validateParams = validates[i].Parameters;
            if (validateParams.Length > 0)
            {
                Array.Copy(validateParams, 0, parameters, offset, validateParams.Length);
                offset += validateParams.Length;
            }
        }

        return new ParameterizedCondition(sb.ToString(), parameters, hasClaimList);
    }

    private static string FormatReadableColumns(IReadOnlyList<string> columns)
    {
        var sb = new StringBuilder(columns.Count * 12);
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(RulesCompiler.Quote(columns[i]));
        }

        return sb.ToString();
    }

    private ValueTask<TableMetadata?> GetTableMetadataAsync(ISqlDatabaseSession session, string table, CancellationToken cancellationToken)
    {
        var epoch = session.SchemaEpoch;
        var key = new TableCacheKey(session.DatabaseId, session.SessionIncarnation, table, epoch);
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
        ulong nonDeterministicMask = 0;
        for (var i = 0; i < raw.Count; i++)
        {
            var col = raw[i];
            columns[i] = col;
            names[i] = col.Name;
            quotedNames[i] = RulesCompiler.Quote(col.Name);
            columnIndex.TryAdd(col.Name, i);
            if (i < 64 && !IsDeterministicDefault(col.DefaultSql))
            {
                nonDeterministicMask |= 1UL << i;
            }
        }

        var (metadataChecked, hasTriggers, hasOnDeleteForeignKeys) = await LoadCoalesceSafetyAsync(session, table, cancellationToken);
        var frozenIndex = columnIndex.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        var metadata = new TableMetadata(
            columns,
            names,
            quotedNames,
            RulesCompiler.Quote(table),
            frozenIndex,
            nonDeterministicMask,
            frozenIndex.ContainsKey("rowid"),
            AsciiIgnoreCaseComparer.Instance.Equals(table, "_b"),
            CanCoalesceCreate: !hasTriggers,
            CanCoalesceDelete: !hasTriggers && !hasOnDeleteForeignKeys,
            CoalesceMetadataChecked: metadataChecked);
        if (metadataChecked && session.SchemaEpoch == epoch && session.SessionIncarnation == key.SessionIncarnation)
        {
            StoreTableMetadata(key, metadata);
        }

        return metadata;
    }

    private static async ValueTask<(bool Checked, bool HasTriggers, bool HasOnDeleteForeignKeys)> LoadCoalesceSafetyAsync(
        ISqlDatabaseSession session, string table, CancellationToken cancellationToken)
    {
        var request = new SqlStatementRequest(
            session.DatabaseId,
            [
                new SqlStatement(
                    "SELECT 1 FROM sqlite_master WHERE type = 'trigger' AND lower(tbl_name) = lower(?) LIMIT 1",
                    [SqlValue.FromText(table)],
                    SqlBuild.NoNamed),
                new SqlStatement(
                    "SELECT 1 FROM sqlite_master WHERE type = 'table' AND instr(lower(sql), 'on delete') > 0 LIMIT 1",
                    [],
                    SqlBuild.NoNamed),
            ],
            SqlAccess.Read,
            ConsistencyMode.Fast,
            CausalBookmark.Empty);
        var outcome = await session.ExecuteReadAsync(request, cancellationToken);
        return outcome switch
        {
            SqlReadResult { ResultSets: [var triggers, var foreignKeys] } => (true, triggers.Rows.Count > 0, foreignKeys.Rows.Count > 0),
            SqlReadResult or SqlError or StorageBusy or StorageTimeout => (false, true, true),
        };
    }

    private static bool IsDeterministicDefault(string? defaultSql)
    {
        if (string.IsNullOrWhiteSpace(defaultSql))
        {
            return true;
        }

        if (!SqlText.TryTokenize(defaultSql, out var tokens) || tokens.Count == 0)
        {
            return false;
        }

        var start = 0;
        var end = tokens.Count;
        while (end - start >= 2 && tokens[start].Kind == SqlTokenKind.Open && tokens[end - 1].Kind == SqlTokenKind.Close)
        {
            start++;
            end--;
        }

        var count = end - start;
        if (count == 1)
        {
            var token = tokens[start];
            return token.Kind is SqlTokenKind.String or SqlTokenKind.Number or SqlTokenKind.Blob
                || (token.Kind == SqlTokenKind.Word && (token.Is("NULL") || token.Is("TRUE") || token.Is("FALSE")));
        }

        return count == 2
            && tokens[start] is { Kind: SqlTokenKind.Symbol, Value: "-" or "+" }
            && tokens[start + 1].Kind == SqlTokenKind.Number;
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

    private void StoreTablePlan(RowTablePlanKey key, RowTablePlan plan)
    {
        if (!_planCache.ContainsKey(key) && Volatile.Read(ref _planCacheCount) >= MaxTableCacheEntries)
        {
            _planCache.Clear();
            Volatile.Write(ref _planCacheCount, 0);
        }

        if (_planCache.TryAdd(key, plan))
        {
            Interlocked.Increment(ref _planCacheCount);
        }
        else
        {
            _planCache[key] = plan;
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
        ResolvedDatabase target, ISqlDatabaseSession session, IReadOnlyList<RowCommand> commands, PlannedRow[] planned, long renderTime, CancellationToken cancellationToken)
    {
        var begun = await coordinator.BeginTransactionAsync(session, new SqlTransactionOptions(target.Database.DefaultConsistency, SqlAccess.Write, CausalBookmark.Empty), cancellationToken);
        return begun switch
        {
            ISqlTransaction transaction => await RunBatchTransactionAsync(target, session, transaction, commands, planned, renderTime, cancellationToken),
            StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
            ClusterTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
        };
    }

    private async ValueTask<RowsOutcome> RunBatchTransactionAsync(
        ResolvedDatabase target, ISqlDatabaseSession session, ISqlTransaction transaction, IReadOnlyList<RowCommand> commands, PlannedRow[] planned, long renderTime, CancellationToken cancellationToken)
    {
        await using (transaction)
        {
            var results = new SqlResultSet[planned.Length];
            for (var index = 0; index < planned.Length; index++)
            {
                var step = await transaction.ExecuteAsync(planned[index].Write, cancellationToken);
                (RowsOutcome? Failure, bool RowRefused) stepFailure = step switch
                {
                    SqlResultSet result => CollectOrRefuse(results, result, commands[index], index),
                    SqlError error => (new RowsOutcome(SqlRefusal.From(error)), false),
                    StorageBusy busy => (new RowsOutcome(SqlRefusal.From(busy)), false),
                    StorageTimeout timeout => (new RowsOutcome(SqlRefusal.From(timeout)), false),
                };
                if (stepFailure.Failure is { } refused)
                {
                    var validation = stepFailure.RowRefused
                        ? await CheckFailureInTransactionAsync(transaction, target.EndUser, commands[index], planned[index], index, renderTime, cancellationToken)
                        : null;
                    await transaction.RollbackAsync(cancellationToken);
                    return validation ?? refused;
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

    private static (RowsOutcome? Failure, bool RowRefused) CollectOrRefuse(SqlResultSet[] results, SqlResultSet result, RowCommand command, int index)
    {
        if (result.Rows.Count == 0)
        {
            return (new RowsOutcome(new RowRefused(
                command.Action == RowAction.Create ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound,
                command.Action == RowAction.Create ? "rule_denied" : "row_not_found",
                command.Action == RowAction.Create ? "the rules refused the row" : "the row does not exist or the rules hide it",
                new RowsErrorDetails(Table: command.Table, Index: index))), true);
        }

        results[index] = result;
        return (null, false);
    }

    private async ValueTask<RowsOutcome> AnswersOrValidateAsync(
        ISqlDatabaseSession session,
        ResolvedDatabase target,
        IReadOnlyList<SqlResultSet> results,
        IReadOnlyList<RowCommand> commands,
        PlannedRow[] planned,
        bool batch,
        bool wrote,
        long renderTime,
        CancellationToken cancellationToken)
    {
        var count = Math.Min(commands.Count, results.Count);
        for (var index = 0; index < count; index++)
        {
            if (results[index].Rows.Count == 0)
            {
                if (BuildCheckStatement(planned[index], target.EndUser, commands[index].Table, renderTime) is { } check)
                {
                    var read = await session.ExecuteReadAsync(
                        new SqlStatementRequest(target.Database.Id, [check], SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty),
                        cancellationToken);
                    var validation = read switch
                    {
                        SqlReadResult answered when answered.ResultSets.Count > 0 => CheckSingleResult(answered.ResultSets[0], commands[index], index, batch),
                        SqlReadResult => null,
                        SqlError error => new RowsOutcome(SqlRefusal.From(error)),
                        StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
                        StorageTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
                    };
                    if (validation is { } refusedValidation)
                    {
                        return refusedValidation;
                    }
                }

                break;
            }
        }

        return Answers(target, results, commands, batch, wrote);
    }

    private static async ValueTask<RowsOutcome?> CheckFailureInTransactionAsync(
        ISqlTransaction transaction,
        RulesAuth? auth,
        RowCommand command,
        PlannedRow planned,
        int index,
        long renderTime,
        CancellationToken cancellationToken)
    {
        if (BuildCheckStatement(planned, auth, command.Table, renderTime) is not { } check)
        {
            return null;
        }

        var outcome = await transaction.ExecuteAsync(check, cancellationToken);
        return outcome switch
        {
            SqlResultSet result => CheckSingleResult(result, command, index, batch: true),
            SqlError error => new RowsOutcome(SqlRefusal.From(error)),
            StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
            StorageTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
        };
    }

    private static SqlStatement? BuildCheckStatement(PlannedRow planned, RulesAuth? auth, string? tableName, long renderTime)
    {
        if (auth is null || planned.Plan is not { ValidateConditions.Length: > 0 } plan)
        {
            return null;
        }

        var validateConditions = plan.ValidateConditions;
        var positional = planned.Write.Positional;
        var checkSourceArgs = new SqlValue[planned.CheckArgCount];
        for (var i = 0; i < planned.CheckArgCount; i++)
        {
            checkSourceArgs[i] = positional[planned.CheckArgOffset + i];
        }

        if (planned.Action == RowAction.Create && planned.CreateProjected is { } createProjected && plan.CreateClause.Compiled is { } createCompiled)
        {
            var sql = BuildCoalescedCreateCheckSql(createCompiled, validateConditions, createProjected, auth, renderTime);
            return new SqlStatement(sql, checkSourceArgs, SqlBuild.NoNamed);
        }

        if (planned.Action != RowAction.Update || planned.UpdateNext is not { } next || tableName is null)
        {
            return null;
        }

        var quotedTable = plan.Meta.QuotedTable;
        var checkedAllowed = plan.UpdateClause.Compiled is { } updateCompiled
            ? RulesCompiler.Render(updateCompiled, auth, renderTime, newPrefix: "new.", rowPrefix: "row_values.")
            : "1";
        var sb = new StringBuilder(next.Length + 128);
        sb.Append("SELECT ");
        sb.Append(checkedAllowed);
        for (var i = 0; i < validateConditions.Length; i++)
        {
            sb.Append(", ");
            sb.Append(RulesCompiler.Render(validateConditions[i], auth, renderTime, newPrefix: "new."));
        }

        sb.Append(" FROM (SELECT * FROM ");
        sb.Append(quotedTable);
        sb.Append(" WHERE rowid = ?) AS row_values, (SELECT ");
        sb.Append(next);
        sb.Append(" FROM ");
        sb.Append(quotedTable);
        sb.Append(" WHERE rowid = ?) AS new");

        var checkArgs = new SqlValue[checkSourceArgs.Length + 2];
        checkArgs[0] = SqlBuild.Integer(planned.UpdateKey);
        Array.Copy(checkSourceArgs, 0, checkArgs, 1, checkSourceArgs.Length);
        checkArgs[^1] = SqlBuild.Integer(planned.UpdateKey);
        return new SqlStatement(sb.ToString(), checkArgs, SqlBuild.NoNamed);
    }

    private static RowsOutcome? CheckSingleResult(SqlResultSet result, RowCommand command, int index, bool batch)
    {
        if (result.Rows is not [var row, ..] || !IsTrue(row[0]))
        {
            return null;
        }

        for (var clause = 1; clause < row.Count; clause++)
        {
            if (!IsTrue(row[clause]))
            {
                return new RowsOutcome(new RowRefused(StatusCodes.Status400BadRequest, "validation_failed",
                    string.Concat("validate[", (clause - 1).ToString(CultureInfo.InvariantCulture), "] is false for the written row"),
                    new RowsErrorDetails(Table: command.Table, Index: batch ? index : null, Clause: clause - 1)));
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

            answers[index] = BuildRowAnswer(commands[index].Action, result, result.Rows[0]);
        }

        return new RowsOutcome(answers);
    }

    private static RowAnswer BuildRowAnswer(RowAction action, SqlResultSet result, IReadOnlyList<SqlValue> row)
    {
        var key = row[0].TryGetIntegral(out var rowId) ? rowId.ToString(CultureInfo.InvariantCulture) : SqlBuild.Format(row[0]);
        return action == RowAction.Delete ? new RowAnswer(key, null) : new RowAnswer(key, result.Columns, row);
    }

    private readonly record struct Position(int Index, bool Batch)
    {
        public RowsErrorDetails Details(string table, string column) => new(Table: table, Column: column, Index: Batch ? Index : null);
    }

    private static RowPlanOutcome Build(
        RowCommand command, string tableName, RowTablePlan plan, RulesAuth? auth, DateTimeOffset now, long renderTime, Position position)
    {
        if (auth is not null && plan.Rule is null)
        {
            return SqlRefusal.RuleDenied(string.Concat("the rules do not cover ", tableName), position.Index);
        }

        if (command.Action is RowAction.Read or RowAction.Delete && command.Values is null or { Count: 0 })
        {
            return command.Action == RowAction.Read
                ? BuildRead(plan, command.Key ?? 0, auth, renderTime, position.Index)
                : BuildDelete(plan, command.Key ?? 0, auth, renderTime, position.Index);
        }

        var extracted = ExtractValues(command, tableName, plan, auth, now, renderTime, position);
        return extracted switch
        {
            ExtractedValues values => command.Action switch
            {
                RowAction.Read => BuildRead(plan, command.Key ?? 0, auth, renderTime, position.Index),
                RowAction.Delete => BuildDelete(plan, command.Key ?? 0, auth, renderTime, position.Index),
                RowAction.Create => BuildCreate(plan, values, auth, position.Index, renderTime),
                RowAction.Update => BuildUpdate(plan, command.Key ?? 0, values, auth, position.Index, renderTime),
                _ => throw new UnreachableException(),
            },
            RowRefused refused => refused,
            SqlRefusal refusal => refusal,
        };
    }

    private static RowValuesOutcome ExtractValues(
        RowCommand command, string tableName, RowTablePlan plan, RulesAuth? auth, DateTimeOffset now, long renderTime, Position position)
    {
        var meta = plan.Meta;
        var assignedNames = command.Action switch
        {
            RowAction.Create => plan.SetCreateNames,
            RowAction.Update => plan.SetUpdateNames,
            RowAction.Read or RowAction.Delete => FrozenSet<string>.Empty,
            _ => throw new UnreachableException(),
        };
        var assignedEntries = command.Action switch
        {
            RowAction.Create => plan.SetCreateEntries,
            RowAction.Update => plan.SetUpdateEntries,
            RowAction.Read or RowAction.Delete => [],
            _ => throw new UnreachableException(),
        };
        var needsUlid = command.Action switch
        {
            RowAction.Create => plan.CreateNeedsUlid,
            RowAction.Update => plan.UpdateNeedsUlid,
            RowAction.Read or RowAction.Delete => false,
            _ => throw new UnreachableException(),
        };

        var canMask = meta.Columns.Length <= 64 && command.Action is RowAction.Create or RowAction.Update;
        if (canMask && auth is not null)
        {
            for (var i = 0; i < assignedEntries.Length; i++)
            {
                if (assignedEntries[i].ColumnIndex < 0 || (command.Action == RowAction.Update && !assignedEntries[i].ExactColumnName))
                {
                    canMask = false;
                    break;
                }
            }
        }

        if (canMask)
        {
            ulong mask = 0;
            SqlValue[]? masked = null;
            var hasDuplicate = false;

            if (command.Values is { Count: > 0 } rawValues)
            {
                masked = new SqlValue[meta.Columns.Length];
                foreach (var (column, element) in rawValues)
                {
                    if (!meta.ColumnIndex.TryGetValue(column, out var columnIdx))
                    {
                        return new RowRefused(StatusCodes.Status400BadRequest, "unknown_column",
                            string.Concat("the table ", tableName, " has no column ", column), position.Details(tableName, column));
                    }

                    var known = meta.Names[columnIdx];
                    if (SqlWire.Value(element) is not { } value)
                    {
                        return new RowRefused(StatusCodes.Status400BadRequest, "invalid_request",
                            string.Concat(known, " is not a SQL value"), position.Details(tableName, known));
                    }

                    if (auth is not null && (!plan.WritableSet.Contains(known) || assignedNames.Contains(known)))
                    {
                        return new RowRefused(StatusCodes.Status403Forbidden, "column_not_writable",
                            string.Concat(known, " is not writable"), position.Details(tableName, known));
                    }

                    var bit = 1UL << columnIdx;
                    if ((mask & bit) != 0)
                    {
                        hasDuplicate = true;
                        break;
                    }

                    mask |= bit;
                    masked[columnIdx] = value;
                }
            }

            if (!hasDuplicate)
            {
                if (auth is not null && assignedEntries.Length > 0)
                {
                    masked ??= new SqlValue[meta.Columns.Length];
                    var ulid = needsUlid ? Ulid.NewUlid(now) : default;
                    for (var i = 0; i < assignedEntries.Length; i++)
                    {
                        var entry = assignedEntries[i];
                        mask |= 1UL << entry.ColumnIndex;
                        masked[entry.ColumnIndex] = RulesCompiler.SetValue(entry.Expression, auth, renderTime, ulid);
                    }
                }

                return new ExtractedValues(mask, CanUseMask: true, masked, DynamicValues: null);
            }
        }

        List<(int ColumnIndex, string Column, SqlValue Value)>? values = null;
        if (command.Values is { Count: > 0 } fallbackValues)
        {
            values = new List<(int ColumnIndex, string Column, SqlValue Value)>(fallbackValues.Count + (auth is not null ? assignedEntries.Length : 0));
            foreach (var (column, element) in fallbackValues)
            {
                if (!meta.ColumnIndex.TryGetValue(column, out var columnIdx))
                {
                    return new RowRefused(StatusCodes.Status400BadRequest, "unknown_column",
                        string.Concat("the table ", tableName, " has no column ", column), position.Details(tableName, column));
                }

                var known = meta.Names[columnIdx];
                if (SqlWire.Value(element) is not { } value)
                {
                    return new RowRefused(StatusCodes.Status400BadRequest, "invalid_request",
                        string.Concat(known, " is not a SQL value"), position.Details(tableName, known));
                }

                if (auth is not null && (!plan.WritableSet.Contains(known) || assignedNames.Contains(known)))
                {
                    return new RowRefused(StatusCodes.Status403Forbidden, "column_not_writable",
                        string.Concat(known, " is not writable"), position.Details(tableName, known));
                }

                values.Add((columnIdx, known, value));
            }
        }

        if (auth is not null && assignedEntries.Length > 0)
        {
            values ??= new List<(int ColumnIndex, string Column, SqlValue Value)>(assignedEntries.Length);
            var ulid = needsUlid ? Ulid.NewUlid(now) : default;
            for (var i = 0; i < assignedEntries.Length; i++)
            {
                var entry = assignedEntries[i];
                for (var j = values.Count - 1; j >= 0; j--)
                {
                    if (string.Equals(values[j].Column, entry.Column, StringComparison.OrdinalIgnoreCase))
                    {
                        values.RemoveAt(j);
                    }
                }

                values.Add((entry.ColumnIndex, entry.Column, RulesCompiler.SetValue(entry.Expression, auth, renderTime, ulid)));
            }
        }

        return new ExtractedValues(0, CanUseMask: false, MaskedValues: null, values);
    }

    private static RowPlanOutcome BuildRead(RowTablePlan plan, long key, RulesAuth? auth, long renderTime, int index)
    {
        if (auth is null)
        {
            return new PlannedRow(
                new SqlStatement(plan.ReadSql ?? string.Concat("SELECT rowid, ", plan.ReadableColumns, " FROM ", plan.Meta.QuotedTable, " WHERE rowid = ? AND 1"),
                    [SqlBuild.Integer(key)], SqlBuild.NoNamed),
                RowAction.Read, null, null, null, 0, 0, 0);
        }

        if (plan.ReadClause.IsMissing)
        {
            return SqlRefusal.RuleDenied("the rules do not allow this operation", index);
        }

        if (plan.ReadClause.InvalidMessage is { } invalid)
        {
            return new SqlRefusal("rules_invalid", invalid, "42501", index, StatusCodes.Status403Forbidden);
        }

        var resolved = ResolveCondition(plan.ReadClause, auth, newPrefix: null, rowPrefix: null);
        var paramCount = resolved?.Parameters.Length ?? 0;
        var args = new SqlValue[1 + paramCount];
        args[0] = SqlBuild.Integer(key);
        if (resolved is not null && paramCount > 0)
        {
            RulesCompiler.BindParameters(resolved.Parameters, auth, renderTime, args.AsSpan(1, paramCount));
        }

        var sql = ReferenceEquals(resolved, plan.ReadClause.Parameterized) && plan.ReadSql is { } cachedSql
            ? cachedSql
            : string.Concat("SELECT rowid, ", plan.ReadableColumns, " FROM ", plan.Meta.QuotedTable, " WHERE rowid = ? AND ", resolved?.Sql ?? "1");

        return new PlannedRow(new SqlStatement(sql, args, SqlBuild.NoNamed), RowAction.Read, null, null, null, 0, 0, 0);
    }

    private static RowPlanOutcome BuildDelete(RowTablePlan plan, long key, RulesAuth? auth, long renderTime, int index)
    {
        if (auth is null)
        {
            return new PlannedRow(
                new SqlStatement(plan.DeleteSql ?? string.Concat("DELETE FROM ", plan.Meta.QuotedTable, " WHERE rowid = ? AND 1 RETURNING rowid"),
                    [SqlBuild.Integer(key)], SqlBuild.NoNamed),
                RowAction.Delete, null, null, null, 0, 0, 0);
        }

        if (plan.DeleteClause.IsMissing)
        {
            return SqlRefusal.RuleDenied("the rules do not allow this operation", index);
        }

        if (plan.DeleteClause.InvalidMessage is { } invalid)
        {
            return new SqlRefusal("rules_invalid", invalid, "42501", index, StatusCodes.Status403Forbidden);
        }

        var resolved = ResolveCondition(plan.DeleteClause, auth, newPrefix: null, rowPrefix: null);
        var paramCount = resolved?.Parameters.Length ?? 0;
        var args = new SqlValue[1 + paramCount];
        args[0] = SqlBuild.Integer(key);
        if (resolved is not null && paramCount > 0)
        {
            RulesCompiler.BindParameters(resolved.Parameters, auth, renderTime, args.AsSpan(1, paramCount));
        }

        var sql = ReferenceEquals(resolved, plan.DeleteClause.Parameterized) && plan.DeleteSql is { } cachedSql
            ? cachedSql
            : string.Concat("DELETE FROM ", plan.Meta.QuotedTable, " WHERE rowid = ? AND ", resolved?.Sql ?? "1", " RETURNING rowid");

        return new PlannedRow(new SqlStatement(sql, args, SqlBuild.NoNamed), RowAction.Delete, null, null, null, 0, 0, 0);
    }

    private static SqlRefusal? CheckCreateRules(RowTablePlan plan, int index)
    {
        if (plan.CreateClause.IsMissing)
        {
            return SqlRefusal.RuleDenied("the rules do not allow this operation", index);
        }

        if (plan.CreateClause.InvalidMessage is { } invalid)
        {
            return new SqlRefusal("rules_invalid", invalid, "42501", index, StatusCodes.Status403Forbidden);
        }

        if (plan.ValidateInvalid)
        {
            return new SqlRefusal("rules_invalid", "a validate clause no longer compiles against the table", "42501", index, StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static SqlRefusal? CheckUpdateRules(RowTablePlan plan, int index)
    {
        if (plan.UpdateClause.IsMissing)
        {
            return SqlRefusal.RuleDenied("the rules do not allow this operation", index);
        }

        if (plan.UpdateClause.InvalidMessage is { } invalid)
        {
            return new SqlRefusal("rules_invalid", invalid, "42501", index, StatusCodes.Status403Forbidden);
        }

        if (plan.ValidateInvalid)
        {
            return new SqlRefusal("rules_invalid", "a validate clause no longer compiles against the table", "42501", index, StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static RowPlanOutcome BuildCreate(RowTablePlan plan, ExtractedValues extracted, RulesAuth? auth, int index, long renderTime)
    {
        if (extracted.CanUseMask)
        {
            if (extracted.Mask == 0 || extracted.MaskedValues is not { } masked)
            {
                return SqlRefusal.Invalid("row names at least one column", index);
            }

            if (auth is not null && CheckCreateRules(plan, index) is { } ruleFailure)
            {
                return ruleFailure;
            }

            var template = plan.GetOrAddCreateTemplate(extracted.Mask);
            var resolved = ResolveCombinedCondition(
                plan.CreateAndValidateParameterized, plan.CreateClause.Compiled, plan.ValidateConditions, auth, newPrefix: "new.", rowPrefix: null);
            var boundCount = template.Columns.Length;
            var ruleParamCount = resolved?.Parameters.Length ?? 0;
            var args = new SqlValue[boundCount + ruleParamCount];
            for (var i = 0; i < boundCount; i++)
            {
                args[i] = masked[template.Columns[i]];
            }

            if (auth is not null && resolved is not null && ruleParamCount > 0)
            {
                RulesCompiler.BindParameters(resolved.Parameters, auth, renderTime, args.AsSpan(boundCount, ruleParamCount));
            }

            var sql = auth is null || ReferenceEquals(resolved, plan.CreateAndValidateParameterized)
                ? template.FullSql
                : string.Concat(template.InsertPrefix, resolved?.Sql ?? "1", template.ReturningSuffix);

            return new PlannedRow(
                new SqlStatement(sql, args, SqlBuild.NoNamed),
                RowAction.Create,
                plan.ValidateConditions.Length > 0 ? plan : null,
                plan.ValidateConditions.Length > 0 ? template.Projected : null,
                null,
                0,
                boundCount,
                0);
        }

        var values = extracted.DynamicValues;
        if (values is null || values.Count == 0)
        {
            return SqlRefusal.Invalid("row names at least one column", index);
        }

        if (auth is not null && CheckCreateRules(plan, index) is { } failure)
        {
            return failure;
        }

        var meta = plan.Meta;
        var tableColumns = meta.Columns;
        var quotedNames = meta.QuotedNames;
        Span<int> valueByColumn = tableColumns.Length <= 64 ? stackalloc int[tableColumns.Length] : new int[tableColumns.Length];
        valueByColumn.Fill(-1);

        var namesBuilder = new StringBuilder(values.Count * 12);
        var dynamicBoundCount = 0;
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
                    dynamicBoundCount++;
                }
            }
            else
            {
                namesBuilder.Append(RulesCompiler.Quote(item.Column));
            }
        }

        var resolvedDynamic = ResolveCombinedCondition(
            plan.CreateAndValidateParameterized, plan.CreateClause.Compiled, plan.ValidateConditions, auth, newPrefix: "new.", rowPrefix: null);
        var dynamicRuleParams = resolvedDynamic?.Parameters.Length ?? 0;
        var bound = new SqlValue[dynamicBoundCount + dynamicRuleParams];
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

        if (auth is not null && resolvedDynamic is not null && dynamicRuleParams > 0)
        {
            RulesCompiler.BindParameters(resolvedDynamic.Parameters, auth, renderTime, bound.AsSpan(dynamicBoundCount, dynamicRuleParams));
        }

        var names = namesBuilder.ToString();
        var projected = projectedBuilder.ToString();
        var conditionSql = resolvedDynamic?.Sql ?? "1";
        var statementSql = string.Concat("INSERT INTO ", meta.QuotedTable, " (", names, ") SELECT ", names, " FROM (SELECT ", projected, ") AS new WHERE ",
            conditionSql, " RETURNING rowid, ", plan.ReadableColumns);

        return new PlannedRow(
            new SqlStatement(statementSql, bound, SqlBuild.NoNamed),
            RowAction.Create,
            plan.ValidateConditions.Length > 0 ? plan : null,
            plan.ValidateConditions.Length > 0 ? projected : null,
            null,
            0,
            dynamicBoundCount,
            0);
    }

    private static RowPlanOutcome BuildUpdate(RowTablePlan plan, long key, ExtractedValues extracted, RulesAuth? auth, int index, long renderTime)
    {
        if (extracted.CanUseMask)
        {
            if (extracted.Mask == 0 || extracted.MaskedValues is not { } masked)
            {
                return SqlRefusal.Invalid("row names at least one column", index);
            }

            if (auth is not null && CheckUpdateRules(plan, index) is { } ruleFailure)
            {
                return ruleFailure;
            }

            var template = plan.GetOrAddUpdateTemplate(extracted.Mask);
            var resolved = ResolveCombinedCondition(
                plan.UpdateAndValidateParameterized, plan.UpdateClause.Compiled, plan.ValidateConditions, auth,
                newPrefix: "new.", rowPrefix: string.Concat(plan.Meta.QuotedTable, "."));
            var boundCount = template.Columns.Length;
            var ruleParamCount = resolved?.Parameters.Length ?? 0;
            var args = new SqlValue[boundCount * 2 + 1 + ruleParamCount];
            for (var i = 0; i < boundCount; i++)
            {
                var value = masked[template.Columns[i]];
                args[i] = value;
                args[boundCount + 1 + i] = value;
            }

            args[boundCount] = SqlBuild.Integer(key);
            if (auth is not null && resolved is not null && ruleParamCount > 0)
            {
                RulesCompiler.BindParameters(resolved.Parameters, auth, renderTime, args.AsSpan(boundCount * 2 + 1, ruleParamCount));
            }

            var sql = auth is null || ReferenceEquals(resolved, plan.UpdateAndValidateParameterized)
                ? template.FullSql
                : string.Concat(template.UpdatePrefix, resolved?.Sql ?? "1", template.ReturningSuffix);

            return new PlannedRow(
                new SqlStatement(sql, args, SqlBuild.NoNamed),
                RowAction.Update,
                plan.ValidateConditions.Length > 0 ? plan : null,
                null,
                plan.ValidateConditions.Length > 0 ? template.Next : null,
                boundCount + 1,
                boundCount,
                key);
        }

        var values = extracted.DynamicValues;
        if (values is null || values.Count == 0)
        {
            return SqlRefusal.Invalid("row names at least one column", index);
        }

        if (auth is not null && CheckUpdateRules(plan, index) is { } failure)
        {
            return failure;
        }

        var meta = plan.Meta;
        var columns = meta.Names;
        var quotedNames = meta.QuotedNames;
        var table = meta.QuotedTable;
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

        var resolvedDynamic = ResolveCombinedCondition(
            plan.UpdateAndValidateParameterized, plan.UpdateClause.Compiled, plan.ValidateConditions, auth,
            newPrefix: "new.", rowPrefix: string.Concat(table, "."));
        var dynamicRuleParams = resolvedDynamic?.Parameters.Length ?? 0;
        var argsDynamic = new SqlValue[values.Count + 1 + nextArgCount + dynamicRuleParams];
        for (var i = 0; i < values.Count; i++)
        {
            argsDynamic[i] = values[i].Value;
        }

        argsDynamic[values.Count] = SqlBuild.Integer(key);
        var nextArgPos = values.Count + 1;
        var nextBuilder = new StringBuilder(columns.Length * 24);
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
                argsDynamic[nextArgPos++] = values[matched].Value;
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

        if (auth is not null && resolvedDynamic is not null && dynamicRuleParams > 0)
        {
            RulesCompiler.BindParameters(resolvedDynamic.Parameters, auth, renderTime, argsDynamic.AsSpan(values.Count + 1 + nextArgCount, dynamicRuleParams));
        }

        var next = nextBuilder.ToString();
        var statementSql = string.Concat("UPDATE ", table, " SET ", assignmentsBuilder.ToString(), " WHERE rowid = ? AND EXISTS (SELECT 1 FROM (SELECT ", next,
            ") AS new WHERE ", resolvedDynamic?.Sql ?? "1", ") RETURNING rowid, ", plan.ReadableColumns);

        return new PlannedRow(
            new SqlStatement(statementSql, argsDynamic, SqlBuild.NoNamed),
            RowAction.Update,
            plan.ValidateConditions.Length > 0 ? plan : null,
            null,
            plan.ValidateConditions.Length > 0 ? next : null,
            values.Count + 1,
            nextArgCount,
            key);
    }

    private static ParameterizedCondition? ResolveCondition(RuleClause clause, RulesAuth? auth, string? newPrefix, string? rowPrefix)
    {
        if (auth is null)
        {
            return null;
        }

        if (clause.Parameterized is { } parameterized && parameterized.CanUseDirectly(auth))
        {
            return parameterized;
        }

        return clause.Compiled is { } compiled ? RulesCompiler.RenderParameterized(compiled, auth, newPrefix, rowPrefix) : null;
    }

    private static ParameterizedCondition? ResolveCombinedCondition(
        ParameterizedCondition? prebuilt, CompiledCondition? primary, CompiledCondition[] validates, RulesAuth? auth, string? newPrefix, string? rowPrefix)
    {
        if (auth is null)
        {
            return null;
        }

        if (prebuilt is not null && prebuilt.CanUseDirectly(auth))
        {
            return prebuilt;
        }

        if (primary is null)
        {
            return null;
        }

        var renderedPrimary = RulesCompiler.RenderParameterized(primary, auth, newPrefix, rowPrefix);
        if (validates.Length == 0)
        {
            return renderedPrimary;
        }

        var renderedValidates = new ParameterizedCondition[validates.Length];
        for (var i = 0; i < validates.Length; i++)
        {
            renderedValidates[i] = RulesCompiler.RenderParameterized(validates[i], auth, newPrefix: "new.");
        }

        return CombineParameterized(renderedPrimary, renderedValidates);
    }

    private static RowsOutcome? Assign(PlannedRow[] planned, int index, PlannedRow row)
    {
        planned[index] = row;
        return null;
    }

    private readonly record struct TableCacheKey(Ulid DatabaseId, long SessionIncarnation, string Table, ulong SchemaEpoch);

    private readonly record struct RowTablePlanKey(Ulid DatabaseId, long SessionIncarnation, ulong SchemaEpoch, string Table, string RulesVersion);

    private sealed class TableCacheKeyComparer : IEqualityComparer<TableCacheKey>
    {
        public static TableCacheKeyComparer Instance { get; } = new();

        public bool Equals(TableCacheKey x, TableCacheKey y) =>
            x.DatabaseId == y.DatabaseId
            && x.SessionIncarnation == y.SessionIncarnation
            && x.SchemaEpoch == y.SchemaEpoch
            && AsciiIgnoreCaseComparer.Instance.Equals(x.Table, y.Table);

        public int GetHashCode(TableCacheKey obj) =>
            HashCode.Combine(obj.DatabaseId, obj.SessionIncarnation, obj.SchemaEpoch, AsciiIgnoreCaseComparer.Instance.GetHashCode(obj.Table));
    }

    private sealed class RowTablePlanKeyComparer : IEqualityComparer<RowTablePlanKey>
    {
        public static RowTablePlanKeyComparer Instance { get; } = new();

        public bool Equals(RowTablePlanKey x, RowTablePlanKey y) =>
            x.DatabaseId == y.DatabaseId
            && x.SessionIncarnation == y.SessionIncarnation
            && x.SchemaEpoch == y.SchemaEpoch
            && string.Equals(x.RulesVersion, y.RulesVersion, StringComparison.Ordinal)
            && AsciiIgnoreCaseComparer.Instance.Equals(x.Table, y.Table);

        public int GetHashCode(RowTablePlanKey obj) =>
            HashCode.Combine(obj.DatabaseId, obj.SessionIncarnation, obj.SchemaEpoch, StringComparer.Ordinal.GetHashCode(obj.RulesVersion),
                AsciiIgnoreCaseComparer.Instance.GetHashCode(obj.Table));
    }
}
