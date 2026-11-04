using System.Text;
using TerraBase.Engine;
using TerraScale.TerraBase.Contracts;

namespace TerraBase.Features.Sql;

internal static class SqlSandbox
{
    public const string Code = "sql_sandbox";

    private static readonly HashSet<string> DeniedNames =
        [with(StringComparer.OrdinalIgnoreCase), "sqlite_dbpage", "sqlite_temp_master", "sqlite_temp_schema", "sqlite_stmt", "btree_dump", "dbstat"];

    private static readonly HashSet<string> DeniedFunctions =
    [
        with(StringComparer.OrdinalIgnoreCase), "load_extension", "readfile", "writefile", "edit", "fsdir", "lsdir", "zipfile", "sqlar_compress",
        "sqlar_uncompress", "csv", "completion",
    ];

    private static readonly Dictionary<string, string> SchemaTables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqlite_schema"] = $"(SELECT * FROM sqlite_schema WHERE {Visible("name")} AND {Visible("tbl_name")} AND lower(name) <> 'sqlite_sequence')",
        ["sqlite_master"] = $"(SELECT * FROM sqlite_schema WHERE {Visible("name")} AND {Visible("tbl_name")} AND lower(name) <> 'sqlite_sequence')",
        ["sqlite_sequence"] = $"(SELECT * FROM sqlite_sequence WHERE {Visible("name")})",
    };

    private static readonly HashSet<string> Clauses =
    [
        with(StringComparer.OrdinalIgnoreCase), "WHERE", "ORDER", "GROUP", "LIMIT", "OFFSET", "HAVING", "WINDOW", "JOIN", "INNER", "LEFT", "RIGHT",
        "FULL", "OUTER", "CROSS", "NATURAL", "ON", "USING", "UNION", "EXCEPT", "INTERSECT", "INDEXED", "NOT", "RETURNING", "SET", "VALUES", "DEFAULT",
    ];

    private static readonly HashSet<string> WriteTargets = [with(StringComparer.OrdinalIgnoreCase), "INTO", "UPDATE", "TABLE", "ON"];

    public static SqlError Reserved { get; } = Denied("names starting with _tb_, _ts_ or turso_, and the engine's own tables, are reserved");

    public static SqlError Denied(string message) => new("SQLITE_AUTH", message) { Sandbox = true };

    public static SqlError Hidden(string name) => SqlErrors.Error($"no such table: {name}");

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

    public static (string Sql, string Run)? RewriteSchema(string sql, List<SqlToken> tokens, out SqlError? denied)
    {
        denied = null;
        var end = tokens.FindIndex(token => token.Kind == SqlTokenKind.Semicolon);
        end = end < 0 ? tokens.Count : end;
        var trigger = tokens.Count > 2 && tokens[0].Is("CREATE") && tokens.Take(4).Any(token => token.Is("TRIGGER"));
        var found = false;
        var run = new StringBuilder();
        var copied = tokens[0].Start;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
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
            if (!aliased)
            {
                run.Append(" AS \"").Append(token.Value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }

            copied = token.End;
        }

        if (!found)
        {
            return null;
        }

        var stop = tokens[end - 1].End;
        run.Append(sql, copied, stop - copied).Append(sql, stop, tokens[^1].End - stop);
        return (sql[tokens[0].Start..stop], run.ToString());
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
