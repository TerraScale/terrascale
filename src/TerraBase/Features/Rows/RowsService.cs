using System.Diagnostics;
using System.Globalization;
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
        var writes = commands.Any(static command => command.Action != RowAction.Read);
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
        List<PlannedRow> planned = [];
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index].Table is not { Length: > 0 } table)
            {
                return new InvalidInput("table names the table", "invalid_table");
            }

            var tableColumns = await RulesService.TableColumnsAsync(session, table, cancellationToken);
            if (tableColumns.Count == 0)
            {
                return new ResourceNotFound(string.Concat("the database has no table ", table), "unknown_table");
            }

            var built = Build(commands[index], table, tableColumns, set, target.EndUser, now, new Position(index, batch));
            var failure = built switch
            {
                PlannedRow row => Add(planned, row),
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

        var request = new SqlStatementRequest(target.Database.Id, [.. planned.Select(static row => row.Write)], writes ? SqlAccess.Write : SqlAccess.Read,
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

    private async ValueTask<RowsOutcome> ExecuteBatchWriteAsync(
        ResolvedDatabase target, ISqlDatabaseSession session, IReadOnlyList<RowCommand> commands, List<PlannedRow> planned, CancellationToken cancellationToken)
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
        ResolvedDatabase target, ISqlDatabaseSession session, ISqlTransaction transaction, IReadOnlyList<RowCommand> commands, List<PlannedRow> planned, CancellationToken cancellationToken)
    {
        await using (transaction)
        {
            List<SqlResultSet> results = new(planned.Count);
            for (var index = 0; index < planned.Count; index++)
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

    private static RowsOutcome? CollectOrRefuse(List<SqlResultSet> results, SqlResultSet result, RowCommand command, int index)
    {
        if (result.Rows.Count == 0)
        {
            return new RowRefused(
                command.Action == RowAction.Create ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound,
                command.Action == RowAction.Create ? "rule_denied" : "row_not_found",
                command.Action == RowAction.Create ? "the rules refused the row" : "the row does not exist or the rules hide it",
                new RowsErrorDetails(Table: command.Table, Index: index));
        }

        results.Add(result);
        return null;
    }

    private static async ValueTask<RowsOutcome?> ValidateAsync(
        ISqlDatabaseSession session, ResolvedDatabase target, IReadOnlyList<RowCommand> commands, List<PlannedRow> planned, bool batch,
        CancellationToken cancellationToken)
    {
        List<(int Index, SqlStatement Check)> checks = [];
        for (var index = 0; index < planned.Count; index++)
        {
            if (planned[index].Check is { } check)
            {
                checks.Add((index, check));
            }
        }

        if (checks.Count == 0)
        {
            return null;
        }

        var request = new SqlStatementRequest(target.Database.Id, [.. checks.Select(static item => item.Check)], SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty);
        var read = await session.ExecuteReadAsync(request, cancellationToken);
        return read switch
        {
            SqlReadResult answered => Checked(answered.ResultSets, checks, commands, batch),
            SqlError error => new RowsOutcome(SqlRefusal.From(error)),
            StorageBusy busy => new RowsOutcome(SqlRefusal.From(busy)),
            StorageTimeout timeout => new RowsOutcome(SqlRefusal.From(timeout)),
        };
    }

    private static RowsOutcome? Checked(IReadOnlyList<SqlResultSet> results, List<(int Index, SqlStatement Check)> checks, IReadOnlyList<RowCommand> commands, bool batch)
    {
        for (var at = 0; at < checks.Count && at < results.Count; at++)
        {
            if (results[at].Rows is not [var row, ..] || !IsTrue(row[0]))
            {
                continue;
            }

            for (var clause = 1; clause < row.Count; clause++)
            {
                if (!IsTrue(row[clause]))
                {
                    var index = checks[at].Index;
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
        List<RowAnswer> answers = [];
        for (var index = 0; index < commands.Count && index < results.Count; index++)
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
            IReadOnlyList<KeyValuePair<string, SqlValue>>? values = commands[index].Action == RowAction.Delete
                ? null
                : [.. result.Columns.Skip(1).Select((column, at) => new KeyValuePair<string, SqlValue>(column.Name, row[at + 1]))];
            answers.Add(new RowAnswer(key, values));
        }

        return new RowsOutcome(answers);
    }

    private readonly record struct Position(int Index, bool Batch)
    {
        public RowsErrorDetails Details(string table, string column) => new(Table: table, Column: column, Index: Batch ? Index : null);
    }

    private static RowPlanOutcome Build(
        RowCommand command, string tableName, IReadOnlyList<TableColumn> tableColumns, RulesSet? set, RulesAuth? auth, DateTimeOffset now, Position position)
    {
        IReadOnlyList<string> columns = [.. tableColumns.Select(static column => column.Name)];
        var rule = set?.Table(tableName);
        var table = RulesCompiler.Quote(tableName);
        var readable = rule?.Columns is { Count: > 0 } chosen ? string.Join(", ", chosen.Select(RulesCompiler.Quote)) : "*";
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
        var values = new List<(string Column, SqlValue Value)>();
        foreach (var (column, element) in command.Values ?? new Dictionary<string, JsonElement>())
        {
            var known = columns.FirstOrDefault(name => string.Equals(name, column, StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                return new RowRefused(StatusCodes.Status400BadRequest, "unknown_column", string.Concat("the table ", tableName, " has no column ", column),
                    position.Details(tableName, column));
            }

            if (SqlWire.Value(element) is not { } value)
            {
                return new RowRefused(StatusCodes.Status400BadRequest, "invalid_request", string.Concat(known, " is not a SQL value"), position.Details(tableName, known));
            }

            if (auth is not null && (rule?.Writable?.Contains(known, StringComparer.OrdinalIgnoreCase) != true
                || assigned?.Keys.Contains(known, StringComparer.OrdinalIgnoreCase) == true))
            {
                return new RowRefused(StatusCodes.Status403Forbidden, "column_not_writable", string.Concat(known, " is not writable"), position.Details(tableName, known));
            }

            values.Add((known, value));
        }

        if (auth is not null && assigned is not null)
        {
            var ulid = Ulid.NewUlid(now);
            foreach (var (column, expression) in assigned)
            {
                values.RemoveAll(value => string.Equals(value.Column, column, StringComparison.OrdinalIgnoreCase));
                values.Add((column, RulesCompiler.SetValue(expression, auth, now.ToUnixTimeMilliseconds(), ulid)));
            }
        }

        var renderTime = now.ToUnixTimeMilliseconds();
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
            RowAction.Create => Create(table, readable, values, rule, auth, tableColumns, columns, position.Index, renderTime),
            RowAction.Update => Update(table, readable, command.Key ?? 0, values, rule, auth, columns, position.Index, renderTime),
            _ => throw new UnreachableException(),
        };
    }

    private static RowPlanOutcome Create(
        string table, string readable, List<(string Column, SqlValue Value)> values, RulesTable? rule, RulesAuth? auth, IReadOnlyList<TableColumn> tableColumns,
        IReadOnlyList<string> columns, int index, long renderTime)
    {
        if (values.Count == 0)
        {
            return SqlRefusal.Invalid("row names at least one column", index);
        }

        var names = string.Join(", ", values.Select(static value => RulesCompiler.Quote(value.Column)));
        List<string> projection = [];
        List<SqlValue> args = [];
        foreach (var column in tableColumns)
        {
            var matched = values.FindIndex(value => string.Equals(value.Column, column.Name, StringComparison.OrdinalIgnoreCase));
            if (matched >= 0)
            {
                projection.Add(string.Concat("? AS ", RulesCompiler.Quote(column.Name)));
                args.Add(values[matched].Value);
            }
            else if (column.DefaultSql is { Length: > 0 } defaultSql)
            {
                projection.Add(string.Concat("(", defaultSql, ") AS ", RulesCompiler.Quote(column.Name)));
            }
            else
            {
                projection.Add(string.Concat("NULL AS ", RulesCompiler.Quote(column.Name)));
            }
        }

        var projected = string.Join(", ", projection);
        SqlValue[] bound = [.. args];
        var condition = Condition(rule?.Create, auth, RulesOperation.Create, columns, index, renderTime, newPrefix: "new.");
        var validations = Validations(rule, auth, columns, index, renderTime);
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
        string table, string readable, long key, List<(string Column, SqlValue Value)> values, RulesTable? rule, RulesAuth? auth, IReadOnlyList<string> columns,
        int index, long renderTime)
    {
        if (values.Count == 0)
        {
            return SqlRefusal.Invalid("row names at least one column", index);
        }

        var assignments = string.Join(", ", values.Select(static value => string.Concat(RulesCompiler.Quote(value.Column), " = ?")));
        var next = string.Join(", ", columns.Select(column => values.Any(value => value.Column == column)
            ? string.Concat("? AS ", RulesCompiler.Quote(column))
            : string.Concat(table, ".", RulesCompiler.Quote(column), " AS ", RulesCompiler.Quote(column))));
        List<SqlValue> nextArgs = [.. columns.Where(column => values.Any(value => value.Column == column)).Select(column => values.First(value => value.Column == column).Value)];
        List<SqlValue> args = [.. values.Select(static value => value.Value), SqlBuild.Integer(key), .. nextArgs];
        var condition = Condition(rule?.Update, auth, RulesOperation.Update, columns, index, renderTime, newPrefix: "new.", rowPrefix: string.Concat(table, "."));
        var checkCondition = Condition(rule?.Update, auth, RulesOperation.Update, columns, index, renderTime, newPrefix: "new.", rowPrefix: "row_values.");
        var validations = Validations(rule, auth, columns, index, renderTime);
        return condition switch
        {
            string allowed => checkCondition switch
            {
                string checkedAllowed => validations switch
                {
                    IReadOnlyList<string> clauses => new PlannedRow(
                        SqlBuild.Statement(string.Concat("UPDATE ", table, " SET ", assignments, " WHERE rowid = ? AND EXISTS (SELECT 1 FROM (SELECT ", next,
                            ") AS new WHERE ", Conjunction(allowed, clauses), ") RETURNING rowid, ", readable), [.. args]),
                        clauses.Count == 0 ? null : SqlBuild.Statement(string.Concat("SELECT ", checkedAllowed, ", ", string.Join(", ", clauses), " FROM (SELECT * FROM ",
                            table, " WHERE rowid = ?) AS row_values, (SELECT ", next, " FROM ", table, " WHERE rowid = ?) AS new"),
                            [SqlBuild.Integer(key), .. nextArgs, SqlBuild.Integer(key)])),
                    SqlRefusal refusal => refusal,
                },
                SqlRefusal refusal => refusal,
            },
            SqlRefusal refusal => refusal,
        };
    }

    private static string Conjunction(string condition, IReadOnlyList<string> clauses) =>
        clauses.Count == 0 ? condition : string.Concat(condition, " AND ", string.Join(" AND ", clauses));

    private static RowClausesOutcome Validations(RulesTable? rule, RulesAuth? auth, IReadOnlyList<string> columns, int index, long renderTime)
    {
        if (auth is null || rule?.Validate is not { Count: > 0 } clauses)
        {
            return Clauses([]);
        }

        List<string> rendered = [];
        foreach (var clause in clauses)
        {
            var compiled = RulesCompiler.Compile(clause, columns, RulesOperation.Validate) switch
            {
                CompiledCondition ready => ready,
                InvalidInput => null,
            };
            if (compiled is null)
            {
                return new SqlRefusal("rules_invalid", "a validate clause no longer compiles against the table", "42501", index, StatusCodes.Status403Forbidden);
            }

            rendered.Add(RulesCompiler.Render(compiled, auth, renderTime, "new."));
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

    private static RowsOutcome? Add(List<PlannedRow> planned, PlannedRow row)
    {
        planned.Add(row);
        return null;
    }
}
