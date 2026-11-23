using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Rules;
using TerraBase.Features.Sql;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Features.Rows;

internal enum RowAction
{
    Create,
    Read,
    Update,
    Delete,
}

internal sealed record RowCommand(RowAction Action, string? Table, long? Key, IReadOnlyDictionary<string, JsonElement>? Values);

internal sealed class RowsService(DatabaseAccess access, RulesStore rules, IClusterCoordinator coordinator, SqlRunner runner, TimeProvider time)
{
    public const string Rows = "/v1/projects/{project}/databases/{database}/tables/{table}/rows";

    public const string Row = Rows + "/{key}";

    public const string Batch = "/v1/projects/{project}/databases/{database}/rows:batch";

    public const int MaxOperations = 100;

    public async ValueTask<Result<IReadOnlyList<RowAnswer>, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> RunAsync(
        Caller caller, string? project, string? database, IReadOnlyList<RowCommand> commands, CancellationToken cancellationToken) =>
        await access.DataAsync(caller, project, database, cancellationToken) switch
        {
            ResolvedDatabase target => await access.OpenAsync(target.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => await ExecuteAsync(target, session, commands, cancellationToken),
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };

    public static Result<long, Validation> Key(string? raw) =>
        long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key) ? key : new Validation("key must be the row's integer key", "invalid_key");

    private async ValueTask<Result<IReadOnlyList<RowAnswer>, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> ExecuteAsync(
        ResolvedDatabase target, ISqlDatabaseSession session, IReadOnlyList<RowCommand> commands, CancellationToken cancellationToken)
    {
        var writes = commands.Any(static command => command.Action != RowAction.Read);
        if (writes && !target.CanWrite)
        {
            return SqlRefusal.ForbiddenScope(0);
        }

        var set = target.EndUser is null ? null : await rules.CurrentAsync(target.Database.Id, cancellationToken) switch
        {
            StoredRules current => current.Rules,
            NotFound or Unavailable => new RulesSet(new Dictionary<string, RulesTable>()),
        };
        List<SqlStatement> statements = [];
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index].Table is not { Length: > 0 } table)
            {
                return new Validation("table names the table", "invalid_table");
            }

            var columns = await RulesService.ColumnsAsync(session, table, cancellationToken);
            if (columns.Count == 0)
            {
                return new NotFound(string.Concat("the database has no table ", table), "unknown_table");
            }

            var built = Build(commands[index], table, columns, set, target.EndUser, index);
            if (built switch { SqlStatement statement => Add(statements, statement), SqlRefusal refusal => refusal } is { } failure)
            {
                return failure;
            }
        }

        var request = new SqlStatementRequest(target.Database.Id, statements, writes ? SqlAccess.Write : SqlAccess.Read, target.Database.DefaultConsistency, CausalBookmark.Empty);
        var outcome = writes
            ? await coordinator.ExecuteWriteAsync(session, request, cancellationToken) switch
            {
                SqlWriteResult written => Answers(target, written.ResultSets, commands, wrote: true),
                SqlError error => SqlRefusal.From(error),
                SerializationConflict conflict => SqlRefusal.From(conflict),
                SchemaMismatch mismatch => SqlRefusal.From(mismatch),
                Unavailable unavailable => SqlRefusal.From(unavailable),
                RequestTimeout timeout => SqlRefusal.From(timeout),
                StorageBusy busy => SqlRefusal.From(busy),
            }
            : await session.ExecuteReadAsync(request, cancellationToken) switch
            {
                SqlReadResult read => Answers(target, read.ResultSets, commands, wrote: false),
                SqlError error => SqlRefusal.From(error),
                StorageBusy busy => SqlRefusal.From(busy),
                RequestTimeout timeout => SqlRefusal.From(timeout),
            };
        return outcome switch
        {
            IReadOnlyList<RowAnswer> answers => Result(answers),
            SqlRefusal refusal => refusal,
        };
    }

    private static Result<IReadOnlyList<RowAnswer>, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable> Result(IReadOnlyList<RowAnswer> answers) => new(answers);

    private Result<IReadOnlyList<RowAnswer>, SqlRefusal> Answers(ResolvedDatabase target, IReadOnlyList<SqlResultSet> results, IReadOnlyList<RowCommand> commands, bool wrote)
    {
        runner.Meter(target, results, wrote, target.Database.DefaultConsistency);
        List<RowAnswer> answers = [];
        for (var index = 0; index < commands.Count && index < results.Count; index++)
        {
            var result = results[index];
            if (result.Rows.Count == 0)
            {
                return new SqlRefusal(commands[index].Action == RowAction.Create ? "rule_denied" : "row_not_found",
                    commands[index].Action == RowAction.Create ? "the rules refused the row" : "the row does not exist or the rules hide it", "P0002", index,
                    commands[index].Action == RowAction.Create ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound);
            }

            var row = result.Rows[0];
            var key = row[0].TryGetIntegral(out var rowId) ? rowId.ToString(CultureInfo.InvariantCulture) : SqlBuild.Format(row[0]);
            IReadOnlyList<KeyValuePair<string, SqlValue>>? values = commands[index].Action == RowAction.Delete
                ? null
                : [.. result.Columns.Skip(1).Select((column, at) => new KeyValuePair<string, SqlValue>(column.Name, row[at + 1]))];
            answers.Add(new RowAnswer(key, values));
        }

        return answers;
    }

    private Result<SqlStatement, SqlRefusal> Build(RowCommand command, string tableName, IReadOnlyList<string> columns, RulesSet? set, RulesAuth? auth, int index)
    {
        var rule = set?.Table(tableName);
        var table = RulesCompiler.Quote(tableName);
        var readable = rule?.Columns is { Count: > 0 } chosen ? string.Join(", ", chosen.Select(RulesCompiler.Quote)) : "*";
        if (set is not null && auth is not null && rule is null)
        {
            return SqlRefusal.RuleDenied(string.Concat("the rules do not cover ", tableName), index);
        }

        var values = new List<(string Column, SqlValue Value)>();
        foreach (var (column, element) in command.Values ?? new Dictionary<string, JsonElement>())
        {
            var known = columns.FirstOrDefault(name => string.Equals(name, column, StringComparison.OrdinalIgnoreCase));
            if (known is null || SqlWire.Value(element) is not { } value)
            {
                return SqlRefusal.Invalid(string.Concat(column, " is not a column of ", tableName, " or its value is not a SQL value"), index);
            }

            if (auth is not null && rule?.Writable is { } writable && !writable.Contains(known, StringComparer.OrdinalIgnoreCase))
            {
                return new SqlRefusal("column_not_writable", string.Concat(known, " is not writable"), "42501", index, StatusCodes.Status403Forbidden);
            }

            values.Add((known, value));
        }

        return command.Action switch
        {
            RowAction.Read => Condition(rule?.Read, auth, RulesOperation.Read, columns, index) switch
            {
                string condition => SqlBuild.Statement(string.Concat("SELECT rowid, ", readable, " FROM ", table, " WHERE rowid = ? AND ", condition), SqlBuild.Integer(command.Key ?? 0)),
                SqlRefusal refusal => refusal,
            },
            RowAction.Delete => Condition(rule?.Delete, auth, RulesOperation.Delete, columns, index) switch
            {
                string condition => SqlBuild.Statement(string.Concat("DELETE FROM ", table, " WHERE rowid = ? AND ", condition, " RETURNING rowid"), SqlBuild.Integer(command.Key ?? 0)),
                SqlRefusal refusal => refusal,
            },
            RowAction.Create => Create(table, readable, values, rule, auth, columns, index),
            RowAction.Update => Update(table, readable, command.Key ?? 0, values, rule, auth, columns, index),
            _ => throw new UnreachableException(),
        };
    }

    private Result<SqlStatement, SqlRefusal> Create(
        string table, string readable, List<(string Column, SqlValue Value)> values, RulesTable? rule, RulesAuth? auth, IReadOnlyList<string> columns, int index)
    {
        AddSet(values, rule?.Set?.Create, auth);
        if (values.Count == 0)
        {
            return SqlRefusal.Invalid("row names at least one column", index);
        }

        var names = string.Join(", ", values.Select(static value => RulesCompiler.Quote(value.Column)));
        var projected = string.Join(", ", values.Select(static value => string.Concat("? AS ", RulesCompiler.Quote(value.Column))));
        SqlValue[] args = [.. values.Select(static value => value.Value)];
        return Condition(rule?.Create, auth, RulesOperation.Create, columns, index, newPrefix: "new.") switch
        {
            string condition => SqlBuild.Statement(string.Concat("INSERT INTO ", table, " (", names, ") SELECT ", names, " FROM (SELECT ", projected, ") AS new WHERE ",
                condition, " RETURNING rowid, ", readable), args),
            SqlRefusal refusal => refusal,
        };
    }

    private Result<SqlStatement, SqlRefusal> Update(
        string table, string readable, long key, List<(string Column, SqlValue Value)> values, RulesTable? rule, RulesAuth? auth, IReadOnlyList<string> columns, int index)
    {
        AddSet(values, rule?.Set?.Update, auth);
        if (values.Count == 0)
        {
            return SqlRefusal.Invalid("row names at least one column", index);
        }

        var assignments = string.Join(", ", values.Select(static value => string.Concat(RulesCompiler.Quote(value.Column), " = ?")));
        var next = string.Join(", ", columns.Select(column => values.Any(value => value.Column == column)
            ? string.Concat("? AS ", RulesCompiler.Quote(column))
            : string.Concat(table, ".", RulesCompiler.Quote(column), " AS ", RulesCompiler.Quote(column))));
        List<SqlValue> args = [.. values.Select(static value => value.Value), SqlBuild.Integer(key)];
        args.AddRange(columns.Where(column => values.Any(value => value.Column == column)).Select(column => values.First(value => value.Column == column).Value));
        return Condition(rule?.Update, auth, RulesOperation.Update, columns, index, newPrefix: "new.") switch
        {
            string condition => SqlBuild.Statement(string.Concat("UPDATE ", table, " SET ", assignments, " WHERE rowid = ? AND EXISTS (SELECT 1 FROM (SELECT ", next,
                ") AS new WHERE ", condition, ") RETURNING rowid, ", readable), [.. args]),
            SqlRefusal refusal => refusal,
        };
    }

    private void AddSet(List<(string Column, SqlValue Value)> values, IReadOnlyDictionary<string, string>? assignments, RulesAuth? auth)
    {
        if (auth is null || assignments is null)
        {
            return;
        }

        foreach (var (column, expression) in assignments)
        {
            values.RemoveAll(value => string.Equals(value.Column, column, StringComparison.OrdinalIgnoreCase));
            values.Add((column, expression switch
            {
                "auth.uid" => SqlValue.FromText(auth.Uid),
                "auth.org_id" => auth.OrgId is { } organization ? SqlValue.FromText(organization) : SqlValue.Null,
                "auth.org_role" => auth.OrgRole is { } role ? SqlValue.FromText(role) : SqlValue.Null,
                "auth.is_anonymous" => SqlValue.FromIntegral(auth.IsAnonymous ? 1 : 0),
                "request.time" => SqlValue.FromIntegral(time.GetUtcNow().ToUnixTimeMilliseconds()),
                "request.ulid" => SqlValue.FromText(Ulid.NewUlid(time.GetUtcNow()).ToString()),
                _ => Literal(expression),
            }));
        }
    }

    private static SqlValue Literal(string expression) =>
        long.TryParse(expression, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ? SqlValue.FromIntegral(integer)
        : expression is ['\'', .., '\''] ? SqlValue.FromText(expression[1..^1].Replace("''", "'", StringComparison.Ordinal))
        : expression.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? SqlValue.FromIntegral(1)
        : expression.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ? SqlValue.FromIntegral(0)
        : SqlValue.Null;

    private Result<string, SqlRefusal> Condition(string? condition, RulesAuth? auth, RulesOperation operation, IReadOnlyList<string> columns, int index, string? newPrefix = null)
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
            CompiledCondition compiled => RulesCompiler.Render(compiled, auth, time.GetUtcNow(), newPrefix),
            Validation refused => new SqlRefusal("rules_invalid", refused.Message, "42501", index, StatusCodes.Status403Forbidden),
        };
    }

    private static SqlRefusal? Add(List<SqlStatement> statements, SqlStatement statement)
    {
        statements.Add(statement);
        return null;
    }
}
