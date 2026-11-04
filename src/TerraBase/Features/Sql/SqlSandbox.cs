using System.Text;
using TerraBase.Engine;
using TerraScale.TerraBase.Contracts;

namespace TerraBase.Features.Sql;

internal static class SqlSandbox
{
    private static readonly HashSet<string> DeniedNames =
        [with(StringComparer.OrdinalIgnoreCase), "sqlite_dbpage", "sqlite_temp_master", "sqlite_temp_schema", "sqlite_stmt", "btree_dump", "dbstat"];

    private static readonly HashSet<string> DeniedFunctions =
    [
        with(StringComparer.OrdinalIgnoreCase), "load_extension", "readfile", "writefile", "edit", "fsdir", "lsdir", "zipfile", "sqlar_compress",
        "sqlar_uncompress", "csv", "completion",
    ];

    internal const string TriggerGuard = "(SELECT foreign_keys FROM pragma_foreign_keys) = 1";

    private const string ShownSql = $"replace(replace(sql, 'WHEN {TriggerGuard} AND (', 'WHEN ('), ' WHEN {TriggerGuard} BEGIN', ' BEGIN')";

    private const string ConflictColumns =
        "id, kind, table_name, row_key, column_name, winner_value, loser_value, winner_region, winner_node, winner_hlc, winner_transaction_id, winner_state, "
        + "loser_region, loser_node, loser_hlc, loser_transaction_id, loser_state, message, created_at";

    private const string ConflictsView = $"(SELECT {ConflictColumns} FROM _ts_conflicts)";

    private const string NoConflictsView =
        "(SELECT " + "NULL AS id, NULL AS kind, NULL AS table_name, NULL AS row_key, NULL AS column_name, NULL AS winner_value, NULL AS loser_value, NULL AS winner_region, NULL AS winner_node, NULL AS winner_hlc, NULL AS winner_transaction_id, NULL AS winner_state, NULL AS loser_region, NULL AS loser_node, NULL AS loser_hlc, NULL AS loser_transaction_id, NULL AS loser_state, NULL AS message, NULL AS created_at WHERE 0)";

    private static readonly Dictionary<string, string> SchemaTables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqlite_schema"] = $"(SELECT type, name, tbl_name, rootpage, {ShownSql} AS sql FROM sqlite_schema WHERE {Visible("name")} AND {Visible("tbl_name")} AND lower(name) <> 'sqlite_sequence')",
        ["sqlite_master"] = $"(SELECT type, name, tbl_name, rootpage, {ShownSql} AS sql FROM sqlite_schema WHERE {Visible("name")} AND {Visible("tbl_name")} AND lower(name) <> 'sqlite_sequence')",
        ["sqlite_sequence"] = $"(SELECT * FROM sqlite_sequence WHERE {Visible("name")})",
    };

    private static readonly HashSet<string> Clauses =
    [
        with(StringComparer.OrdinalIgnoreCase), "WHERE", "ORDER", "GROUP", "LIMIT", "OFFSET", "HAVING", "WINDOW", "JOIN", "INNER", "LEFT", "RIGHT",
        "FULL", "OUTER", "CROSS", "NATURAL", "ON", "USING", "UNION", "EXCEPT", "INTERSECT", "INDEXED", "NOT", "RETURNING", "SET", "VALUES", "DEFAULT",
    ];

    private static readonly HashSet<string> WriteTargets = [with(StringComparer.OrdinalIgnoreCase), "INTO", "UPDATE", "TABLE", "ON"];

    public static SqlError Reserved { get; } = Denied("names starting with _tb_, _ts_ or turso_, and the engine's own tables, are reserved");

    public static SqlError Denied(string message) => new(SqlErrors.Sandbox, message);

    public static SqlError Hidden { get; } = SqlErrors.Error("no such table");

    public static bool IsHidden(string name) => TerraBaseSqlNames.IsReserved(name);

    public static SqlError? Check(List<SqlToken> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind is not (SqlTokenKind.Word or SqlTokenKind.Quoted or SqlTokenKind.String))
            {
                continue;
            }

            if (DeniedNames.Contains(token.Value))
            {
                return Denied($"{token.Value.ToLowerInvariant()} is not available");
            }

            if (token.Kind == SqlTokenKind.Word && i + 1 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.Open && DeniedFunctions.Contains(token.Value))
            {
                return Denied($"the function {token.Value.ToLowerInvariant()} is not available");
            }
        }

        return null;
    }

    public static (string Sql, string Run, string? WithoutConflicts)? RewriteSchema(string sql, List<SqlToken> tokens, out SqlError? denied)
    {
        denied = null;
        var end = tokens.FindIndex(token => token.Kind == SqlTokenKind.Semicolon);
        end = end < 0 ? tokens.Count : end;
        var trigger = tokens.Count > 2 && tokens[0].Is("CREATE") && tokens.Take(4).Any(token => token.Is("TRIGGER"));
        var found = false;
        var conflicts = false;
        var run = new StringBuilder();
        var empty = new StringBuilder();
        var copied = tokens[0].Start;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Is("terrascale") && i + 2 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.Dot && tokens[i + 2].IsName
                && tokens[i + 2].Value.Equals("conflicts", StringComparison.OrdinalIgnoreCase))
            {
                if (trigger)
                {
                    denied = Denied("triggers cannot read terrascale.conflicts");
                    return null;
                }

                if (i > 0 && tokens[i - 1] is var target && (WriteTargets.Any(target.Is) || (target.Is("FROM") && i > 1 && tokens[i - 2].Is("DELETE"))))
                {
                    denied = Denied("terrascale.conflicts is read-only");
                    return null;
                }

                if (i >= end)
                {
                    continue;
                }

                found = conflicts = true;
                var named = i + 3 < end && tokens[i + 3] is var following
                    && (following.Is("AS") || (following.IsName && !(following.Kind == SqlTokenKind.Word && Clauses.Contains(following.Value))));
                run.Append(sql, copied, token.Start - copied).Append(ConflictsView);
                empty.Append(sql, copied, token.Start - copied).Append(NoConflictsView);
                if (!named)
                {
                    run.Append(" AS conflicts");
                    empty.Append(" AS conflicts");
                }

                copied = tokens[i + 2].End;
                i += 2;
                continue;
            }

            if (!token.IsName || !SchemaTables.TryGetValue(token.Value, out var visible) || (i + 1 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.Dot))
            {
                continue;
            }

            var qualified = i > 0 && tokens[i - 1].Kind == SqlTokenKind.Dot;
            if (qualified && !(i > 1 && tokens[i - 2].Is("main")))
            {
                denied = Denied("only the main schema is readable");
                return null;
            }

            if (trigger)
            {
                denied = Denied("triggers cannot read the schema table");
                return null;
            }

            var first = qualified ? i - 2 : i;
            if (first > 0 && tokens[first - 1] is var before && (WriteTargets.Any(before.Is) || (before.Is("FROM") && first > 1 && tokens[first - 2].Is("DELETE"))))
            {
                denied = Denied("the schema table is read-only");
                return null;
            }

            if (i >= end)
            {
                continue;
            }

            found = true;
            var aliased = i + 1 < end && tokens[i + 1] is var next
                && (next.Is("AS") || (next.IsName && !(next.Kind == SqlTokenKind.Word && Clauses.Contains(next.Value))));
            run.Append(sql, copied, tokens[first].Start - copied).Append(visible);
            empty.Append(sql, copied, tokens[first].Start - copied).Append(visible);
            if (!aliased)
            {
                var alias = " AS \"" + token.Value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                run.Append(alias);
                empty.Append(alias);
            }

            copied = token.End;
        }

        if (!found)
        {
            return null;
        }

        var stop = tokens[end - 1].End;
        run.Append(sql, copied, stop - copied).Append(sql, stop, tokens[^1].End - stop);
        empty.Append(sql, copied, stop - copied).Append(sql, stop, tokens[^1].End - stop);
        return (sql[tokens[0].Start..stop], run.ToString(), conflicts ? empty.ToString() : null);
    }

    public static string? GuardTrigger(string sql, List<SqlToken> tokens)
    {
        var at = tokens.FindIndex(token => token.Is("TRIGGER"));
        if (at is < 0 or > 3)
        {
            return null;
        }

        var depth = 0;
        int? on = null, when = null, begin = null;
        for (var i = at + 1; i < tokens.Count && begin is null; i++)
        {
            var token = tokens[i];
            depth += token.Kind switch { SqlTokenKind.Open => 1, SqlTokenKind.Close => -1, _ => 0 };
            if (depth != 0 || token.Kind != SqlTokenKind.Word)
            {
                continue;
            }

            if (on is null && token.Is("ON"))
            {
                on = i;
            }
            else if (on is not null && when is null && token.Is("WHEN"))
            {
                when = i;
            }
            else if (on is not null && token.Is("BEGIN"))
            {
                begin = i;
            }
        }

        if (begin is not { } body || (when is { } condition && condition + 1 >= body))
        {
            return null;
        }

        var start = tokens[0].Start;
        var bodyStart = tokens[body].Start;
        var rest = sql[bodyStart..tokens[^1].End];
        return when is { } clause
            ? $"{sql[start..tokens[clause].End]} {TriggerGuard} AND ({sql[tokens[clause + 1].Start..tokens[body - 1].End]}) {rest}"
            : $"{sql[start..bodyStart]}WHEN {TriggerGuard} {rest}";
    }

    public static object?[][] HideRows(IReadOnlyList<object?[]> rows, IReadOnlyList<int> columns) =>
        [.. rows.Where(row => !columns.Any(column => column < row.Length && row[column] is string name && (IsHidden(name) || DeniedNames.Contains(name) || name.Equals("sqlite_sequence", StringComparison.OrdinalIgnoreCase))))];

    private static string Visible(string column)
    {
        var names = string.Join(", ", TerraBaseSqlNames.ReservedTableNames.Select(name => $"'{name}'"));
        var prefixes = TerraBaseSqlNames.ReservedPrefixes.Select(prefix => $" AND substr(lower({column}), 1, {prefix.Length}) <> '{prefix}'");
        return $"lower({column}) NOT IN ({names}){string.Concat(prefixes)}";
    }
}
