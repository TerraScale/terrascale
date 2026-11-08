using System.Text;
using TerraBase.Engine;
using TerraScale.TerraBase.Contracts;

namespace TerraBase.Features.Sql;

internal enum SqlStatementKind
{
    Read,
    Write,
    Begin,
    Commit,
    Rollback,
    Savepoint,
    Release,
    RollbackTo,
    Noop,
}

internal enum SqlWriteVerb
{
    None,
    Insert,
    Update,
    Delete,
    Schema,
}

internal sealed record SqlStatementPlan(
    string Sql, SqlStatementKind Kind, SqlWriteVerb Verb, bool ReadsTables, bool IsExplain, bool ReadOnly, IReadOnlyList<string?> Params, IReadOnlyList<string> Names,
    string? Savepoint)
{
    public string Run { get => field ?? Sql; init; }

    public IReadOnlyList<int> HiddenColumns { get; init; } = [];

    public string? RunWithoutConflicts { get; init; }

    public bool GuardsTrigger { get; init; }

    public bool IsControl => Kind is SqlStatementKind.Begin or SqlStatementKind.Commit or SqlStatementKind.Rollback;

    public bool IsSavepoint => Kind is SqlStatementKind.Savepoint or SqlStatementKind.Release or SqlStatementKind.RollbackTo;
}

internal readonly union SqlPlanOutcome(SqlStatementPlan, SqlError);

internal static class SqlScript
{
    public const int MaxSqlLength = 16 * 1024;

    private static readonly HashSet<string> ReadPragmas =
    [
        with(StringComparer.OrdinalIgnoreCase), "table_info", "table_xinfo", "table_list", "index_list", "index_info", "index_xinfo", "foreign_key_list",
        "foreign_key_check",
    ];

    public static bool IsReservedName(string name) => TerraBaseSqlNames.IsInternalTable(name);

    public static int CountAtMost(string script)
    {
        if (!SqlText.TryTokenize(script, out var tokens))
        {
            return 1;
        }

        var count = 0;
        var open = false;
        foreach (var token in tokens)
        {
            count += !open && token.Kind != SqlTokenKind.Semicolon ? 1 : 0;
            open = token.Kind != SqlTokenKind.Semicolon;
        }

        return count;
    }

    public static bool HasStatement(string script) =>
        !SqlText.TryTokenize(script, out var tokens) || tokens.Exists(token => token.Kind != SqlTokenKind.Semicolon);

    public static bool IsSimple(string script) =>
        SqlText.TryTokenize(script, out var tokens) && tokens.Find(token => token.Kind != SqlTokenKind.Semicolon) is { Kind: SqlTokenKind.Word, Value: { } first }
        && first.ToUpperInvariant() is "BEGIN" or "START" or "COMMIT" or "END" or "ROLLBACK" or "SAVEPOINT" or "RELEASE" or "PRAGMA";

    public static int SimpleLength(string script) =>
        SqlText.TryTokenize(script, out var tokens) && tokens.FindIndex(token => token.Kind == SqlTokenKind.Semicolon) is var end and >= 0
            ? tokens[end].End
            : script.Length;

    public static SqlPlanOutcome Classify(string? sql, bool bounded = true)
    {
        if (string.IsNullOrWhiteSpace(sql) || (bounded && Encoding.UTF8.GetByteCount(sql) > MaxSqlLength))
        {
            return SqlErrors.Error("sql must be 1 to 16384 UTF-8 bytes");
        }

        if (!SqlText.TryTokenize(sql, out var all))
        {
            return SqlErrors.Error("SQL syntax error: unterminated string, quoted name or comment");
        }

        var start = all.FindIndex(token => token.Kind != SqlTokenKind.Semicolon);
        if (start < 0)
        {
            return SqlErrors.Error("the statement is empty");
        }

        var tokens = all[start..(all.FindLastIndex(token => token.Kind != SqlTokenKind.Semicolon) + 1)];
        var first = tokens[0];
        var verb = first.Kind == SqlTokenKind.Word ? first.Value.ToUpperInvariant() : "";
        var names = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Kind is not (SqlTokenKind.Word or SqlTokenKind.Quoted or SqlTokenKind.String))
            {
                continue;
            }

            if (SqlSandbox.IsHidden(token.Value))
            {
                return verb is "CREATE" or "ALTER" ? SqlSandbox.Reserved : SqlSandbox.Hidden;
            }

            names.Add(token.Value.ToLowerInvariant());
        }

        if (SqlSandbox.Check(tokens) is { } denied)
        {
            return denied;
        }

        if (UsesFullJoin(tokens))
        {
            return SqlErrors.Error("full outer join is not supported; use LEFT JOIN with UNION ALL");
        }

        var text = sql[tokens[0].Start..tokens[^1].End];
        var savepoint = tokens[^1].IsName ? tokens[^1].Value : null;
        var parameters = Parameters(tokens);
        SqlStatementPlan Plan(SqlStatementKind kind, SqlWriteVerb write = SqlWriteVerb.None, bool readsTables = false, bool explain = false, bool readOnly = false)
        {
            return new(text, kind, write, readsTables, explain, readOnly, parameters, names, savepoint);
        }

        var outcome = verb switch
        {
            "SELECT" or "VALUES" => Plan(SqlStatementKind.Read, readsTables: tokens.Exists(token => token.Is("FROM"))),
            "EXPLAIN" => Explained(sql, tokens) is { } refused ? refused : Plan(SqlStatementKind.Read, explain: true),
            "WITH" => TopLevelWrite(tokens) is { } dml
                ? Plan(SqlStatementKind.Write, Verb(dml))
                : Plan(SqlStatementKind.Read, readsTables: tokens.Exists(token => token.Is("FROM"))),
            "INSERT" or "REPLACE" or "UPDATE" or "DELETE" => Plan(SqlStatementKind.Write, Verb(verb)),
            "CREATE" when tokens.Count > 1 && (tokens[1].Is("TEMP") || tokens[1].Is("TEMPORARY")) => SqlSandbox.Denied("temporary tables, views and triggers are not supported"),
            "CREATE" when tokens.Count > 1 && tokens[1].Is("VIRTUAL") => SqlSandbox.Denied("virtual tables are not supported"),
            "CREATE" or "DROP" or "ALTER" or "REINDEX" => Plan(SqlStatementKind.Write, SqlWriteVerb.Schema),
            "ATTACH" or "DETACH" or "VACUUM" or "ANALYZE" => SqlSandbox.Denied($"{verb} is not supported"),
            "BEGIN" => Begin(tokens, 1, Plan),
            "START" when tokens.Count > 1 && tokens[1].Is("TRANSACTION") => Begin(tokens, 2, Plan),
            "COMMIT" or "END" when Only(tokens, 1, "TRANSACTION") => Plan(SqlStatementKind.Commit),
            "ROLLBACK" when tokens.Exists(token => token.Is("TO")) => Plan(SqlStatementKind.RollbackTo),
            "ROLLBACK" when Only(tokens, 1, "TRANSACTION") => Plan(SqlStatementKind.Rollback),
            "SAVEPOINT" => Plan(SqlStatementKind.Savepoint),
            "RELEASE" => Plan(SqlStatementKind.Release),
            "PRAGMA" => Pragma(tokens, Plan),
            "COMMIT" or "END" or "ROLLBACK" or "START" => SqlErrors.Error("SQL syntax error in a transaction statement"),
            _ => Plan(SqlStatementKind.Read),
        };
        if (verb == "PRAGMA" || outcome is not SqlStatementPlan { Kind: SqlStatementKind.Read or SqlStatementKind.Write } plan)
        {
            return outcome;
        }

        var rewritten = SqlSandbox.RewriteSchema(sql, tokens, out var schemaDenied);
        if (schemaDenied is not null)
        {
            return schemaDenied;
        }

        if (rewritten is var (original, run, withoutConflicts))
        {
            return plan with { Sql = original, Run = run, RunWithoutConflicts = withoutConflicts };
        }

        return verb == "CREATE" && SqlSandbox.GuardTrigger(sql, tokens) is { } guarded ? plan with { Run = guarded, GuardsTrigger = true } : plan;
    }

    private static bool UsesFullJoin(List<SqlToken> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Is("FULL"))
            {
                continue;
            }

            var next = i + 1;
            if (next < tokens.Count && tokens[next].Is("OUTER"))
            {
                next++;
            }

            if (next < tokens.Count && tokens[next].Is("JOIN"))
            {
                return true;
            }
        }

        return false;
    }


    private static SqlError? Explained(string sql, List<SqlToken> tokens)
    {
        var body = 1;
        while (body < tokens.Count && (tokens[body].Is("QUERY") || tokens[body].Is("PLAN")))
        {
            body++;
        }

        return body < tokens.Count && Classify(sql[tokens[body].Start..tokens[^1].End], bounded: false) is SqlError refused ? refused : null;
    }

    private static SqlWriteVerb Verb(string verb) => verb switch
    {
        "INSERT" or "REPLACE" => SqlWriteVerb.Insert,
        "UPDATE" => SqlWriteVerb.Update,
        _ => SqlWriteVerb.Delete,
    };

    private static string? TopLevelWrite(List<SqlToken> tokens)
    {
        var depth = 0;
        foreach (var token in tokens)
        {
            depth += token.Kind switch { SqlTokenKind.Open => 1, SqlTokenKind.Close => -1, _ => 0 };
            if (depth == 0 && token.Kind == SqlTokenKind.Word && token.Value.ToUpperInvariant() is "INSERT" or "REPLACE" or "UPDATE" or "DELETE")
            {
                return token.Value.ToUpperInvariant();
            }
        }

        return null;
    }

    private static bool Only(List<SqlToken> tokens, int from, params string[] words) =>
        tokens.Skip(from).All(token => words.Any(token.Is));

    private static SqlPlanOutcome Begin(
        List<SqlToken> tokens, int from, Func<SqlStatementKind, SqlWriteVerb, bool, bool, bool, SqlStatementPlan> plan)
    {
        if (!Only(tokens, from, "DEFERRED", "IMMEDIATE", "EXCLUSIVE", "TRANSACTION", "READ", "ONLY", "READONLY", "WRITE"))
        {
            return SqlErrors.Error("SQL syntax error in a transaction statement");
        }

        var readOnly = tokens.Exists(token => token.Is("READONLY"))
            || tokens.Select((token, i) => token.Is("READ") && i + 1 < tokens.Count && tokens[i + 1].Is("ONLY")).Any(match => match);
        return plan(SqlStatementKind.Begin, SqlWriteVerb.None, false, false, readOnly);
    }

    private static SqlPlanOutcome Pragma(List<SqlToken> tokens, Func<SqlStatementKind, SqlWriteVerb, bool, bool, bool, SqlStatementPlan> plan)
    {
        var at = 1;
        if (tokens.Count > 3 && tokens[2].Kind == SqlTokenKind.Dot)
        {
            if (!tokens[1].Is("main"))
            {
                return SqlSandbox.Denied("PRAGMA reads only the main schema");
            }

            at = 3;
        }

        if (at >= tokens.Count || tokens[at].Kind != SqlTokenKind.Word)
        {
            return SqlErrors.Error("SQL syntax error in a PRAGMA");
        }

        var name = tokens[at].Value;
        var rest = tokens.Skip(at + 1).ToList();
        var value = rest switch
        {
            [] => null,
            [{ Kind: SqlTokenKind.Symbol, Value: "=" }, var set] => set.Value,
            [{ Kind: SqlTokenKind.Open }, var argument, { Kind: SqlTokenKind.Close }] => argument.Value,
            _ => "",
        };
        if (value is not null && SqlSandbox.IsHidden(value))
        {
            return SqlSandbox.Hidden;
        }

        if (name.Equals("foreign_keys", StringComparison.OrdinalIgnoreCase))
        {
            return value switch
            {
                null => plan(SqlStatementKind.Read, SqlWriteVerb.None, false, false, false),
                _ when value.ToUpperInvariant() is "ON" or "1" or "TRUE" or "YES" => plan(SqlStatementKind.Noop, SqlWriteVerb.None, false, false, false),
                _ when value.ToUpperInvariant() is "OFF" or "0" or "FALSE" or "NO" => SqlSandbox.Denied("foreign keys are always enforced"),
                _ => SqlErrors.Error("PRAGMA foreign_keys takes ON or OFF"),
            };
        }

        if (!ReadPragmas.Contains(name))
        {
            return SqlSandbox.Denied("this PRAGMA is not supported");
        }

        if (rest is [{ Kind: SqlTokenKind.Symbol, Value: "=" }, ..] || value == "")
        {
            return SqlSandbox.Denied("this PRAGMA only reads");
        }

        var read = plan(SqlStatementKind.Read, SqlWriteVerb.None, false, false, false);
        return name.ToLowerInvariant() switch
        {
            "table_list" => read with { HiddenColumns = [1] },
            "foreign_key_check" when value is null => read with { HiddenColumns = [0, 2] },
            _ => read,
        };
    }

    private static List<string?> Parameters(List<SqlToken> tokens)
    {
        var slots = new List<string?>();
        var named = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens.Where(token => token.Kind == SqlTokenKind.Parameter))
        {
            if (token.Value == "?")
            {
                slots.Add(null);
            }
            else if (token.Value[0] == '?')
            {
                if (int.TryParse(token.Value.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && index is > 0 and <= 32766)
                {
                    while (slots.Count < index)
                    {
                        slots.Add(null);
                    }
                }
            }
            else if (!named.ContainsKey(token.Value))
            {
                named[token.Value] = slots.Count;
                slots.Add(token.Value);
            }
        }

        return slots;
    }
}
