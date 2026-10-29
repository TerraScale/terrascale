using TerraBase.Engine;

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
    string Sql, SqlStatementKind Kind, SqlWriteVerb Verb, bool ReadsTables, bool IsExplain, bool ReadOnly, IReadOnlyList<string?> Params, IReadOnlyList<string> Names)
{
    public bool IsControl => Kind is SqlStatementKind.Begin or SqlStatementKind.Commit or SqlStatementKind.Rollback;

    public bool IsSavepoint => Kind is SqlStatementKind.Savepoint or SqlStatementKind.Release or SqlStatementKind.RollbackTo;
}

internal readonly union SqlPlanOutcome(SqlStatementPlan, SqlError);

internal readonly union SqlScriptOutcome(List<string>, SqlError);

internal static class SqlScript
{
    public const int MaxSqlLength = 16 * 1024;

    private static readonly string[] ReservedPrefixes = ["_tb_", "turso_", "__turso_", "tb_index_", "fts_dir_", "pragma_", "sqlite_stat"];

    private static readonly HashSet<string> ReservedTables = [with(StringComparer.OrdinalIgnoreCase), "collections", "commit_offset", "_collections", "_indexes", "_unique"];

    private static readonly HashSet<string> ReadPragmas =
    [
        with(StringComparer.OrdinalIgnoreCase), "table_info", "table_xinfo", "table_list", "index_list", "index_info", "index_xinfo", "foreign_key_list",
        "foreign_key_check",
    ];

    private static readonly SqlError Reserved = SqlErrors.Error("names starting with _tb_ or turso_, and the engine's own tables, are reserved");

    public static bool IsReservedName(string name) => IsReservedTable(name) || name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    public static SqlScriptOutcome Split(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (!SqlText.TryTokenize(script, out var tokens))
        {
            return SqlErrors.Error("SQL syntax error: unterminated string, quoted name or comment");
        }

        return SqlText.Statements(tokens).Select(statement => SqlText.Text(script, statement)).ToList();
    }

    public static SqlPlanOutcome Classify(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > MaxSqlLength)
        {
            return SqlErrors.Error("sql must be 1 to 16384 characters");
        }

        if (!SqlText.TryTokenize(sql, out var all))
        {
            return SqlErrors.Error("SQL syntax error: unterminated string, quoted name or comment");
        }

        var statements = SqlText.Statements(all);
        if (statements is not [var tokens])
        {
            return SqlErrors.Error(statements.Count == 0 ? "the statement is empty" : "run one statement at a time, or send a script");
        }

        var names = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Kind is not (SqlTokenKind.Word or SqlTokenKind.Quoted or SqlTokenKind.String))
            {
                continue;
            }

            if (IsReservedTable(token.Value))
            {
                return Reserved;
            }

            names.Add(token.Value.ToLowerInvariant());
        }

        var text = SqlText.Text(sql, tokens);
        var parameters = Parameters(tokens);
        var first = tokens[0];
        var verb = first.Kind == SqlTokenKind.Word ? first.Value.ToUpperInvariant() : "";
        SqlStatementPlan Plan(SqlStatementKind kind, SqlWriteVerb write = SqlWriteVerb.None, bool readsTables = false, bool explain = false, bool readOnly = false)
        {
            return new(text, kind, write, readsTables, explain, readOnly, parameters, names);
        }

        return verb switch
        {
            "SELECT" or "VALUES" => Plan(SqlStatementKind.Read, readsTables: tokens.Exists(token => token.Is("FROM"))),
            "EXPLAIN" => Plan(SqlStatementKind.Read, explain: true),
            "WITH" => TopLevelWrite(tokens) is { } dml
                ? Plan(SqlStatementKind.Write, Verb(dml))
                : Plan(SqlStatementKind.Read, readsTables: tokens.Exists(token => token.Is("FROM"))),
            "INSERT" or "REPLACE" or "UPDATE" or "DELETE" => Plan(SqlStatementKind.Write, Verb(verb)),
            "CREATE" when tokens.Count > 1 && (tokens[1].Is("TEMP") || tokens[1].Is("TEMPORARY")) => SqlErrors.Error("temporary tables, views and triggers are not supported"),
            "CREATE" when tokens.Count > 1 && tokens[1].Is("VIRTUAL") => SqlErrors.Error("virtual tables are not supported"),
            "CREATE" or "DROP" or "ALTER" or "REINDEX" => Plan(SqlStatementKind.Write, SqlWriteVerb.Schema),
            "ATTACH" or "DETACH" or "VACUUM" or "ANALYZE" => SqlErrors.Error($"{verb} is not supported"),
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
    }

    private static bool IsReservedTable(string name) => ReservedTables.Contains(name) || HasReservedPrefix(name);

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

    private static bool HasReservedPrefix(string name) => ReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

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
                return SqlErrors.Error("PRAGMA reads only the main schema");
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
        if (value is not null && IsReservedTable(value))
        {
            return Reserved;
        }

        if (name.Equals("foreign_keys", StringComparison.OrdinalIgnoreCase))
        {
            return value switch
            {
                null => plan(SqlStatementKind.Read, SqlWriteVerb.None, false, false, false),
                _ when value.ToUpperInvariant() is "ON" or "1" or "TRUE" or "YES" => plan(SqlStatementKind.Noop, SqlWriteVerb.None, false, false, false),
                _ when value.ToUpperInvariant() is "OFF" or "0" or "FALSE" or "NO" => SqlErrors.Error("foreign keys are always enforced"),
                _ => SqlErrors.Error("PRAGMA foreign_keys takes ON or OFF"),
            };
        }

        if (!ReadPragmas.Contains(name))
        {
            return SqlErrors.Error("this PRAGMA is not supported");
        }

        return rest is [{ Kind: SqlTokenKind.Symbol, Value: "=" }, ..] || value == ""
            ? SqlErrors.Error("this PRAGMA only reads")
            : plan(SqlStatementKind.Read, SqlWriteVerb.None, false, false, false);
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

internal static class SqlErrors
{
    public static SqlError Error(string message) => new("SQLITE_ERROR", message);

    public static SqlError NonFinite { get; } = Error("nonfinite numbers are not supported");

    public static SqlError Auth { get; } = new("SQLITE_AUTH", "writes and schema changes need the documents:write scope");

    public static SqlError Busy(string message) => new("SQLITE_BUSY", message);

    public static SqlError TooBig(string message) => new("SQLITE_TOOBIG", message);

    public static SqlError Interrupt { get; } = new("SQLITE_INTERRUPT", "query too expensive: it exceeds the work budget");

    public static SqlError Constraint(string kind, string message) => new("SQLITE_CONSTRAINT_" + kind, message);
}
