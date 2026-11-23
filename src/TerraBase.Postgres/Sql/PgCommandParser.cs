using System.Text;
using TerraBase.Postgres.Protocol;
using TerraScale.Contracts.Consistency;

namespace TerraBase.Postgres.Sql;

internal static class PgCommandParser
{
    public static Result<IReadOnlyList<PgCommand>, PgError> Parse(string sql, PgRewriteContext context) =>
        PgLexer.Tokenize(sql) switch
        {
            PgToken[] tokens => ParseTokens(sql, tokens, context),
            PgError error => error,
        };

    public static Result<PgSqlCommand, PgError> ParseQuery(string sql, PgRewriteContext context) =>
        Parse(sql, context) switch
        {
            IReadOnlyList<PgCommand> and [PgSqlCommand query] => query,
            IReadOnlyList<PgCommand> => PgError.Syntax("COPY requires a single query"),
            PgError error => error,
        };

    private static Result<IReadOnlyList<PgCommand>, PgError> ParseTokens(string sql, PgToken[] tokens, PgRewriteContext context)
    {
        List<PgCommand> commands = [];
        foreach (var (start, length) in Split(sql, tokens))
        {
            var editor = PgTokenEditor.Create(sql, tokens.AsSpan(start, length));
            if (editor.Count == 0)
            {
                continue;
            }

            if (!PgRewrite.Fits(sql.AsSpan(editor.Start(0), editor.End(editor.Count - 1) - editor.Start(0))))
            {
                return PgRewrite.StatementTooLong;
            }

            var failure = ParseStatement(editor, context) switch
            {
                PgCommand command => Collect(commands, command),
                PgError error => error,
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        if (commands.Count == 0)
        {
            commands.Add(new PgEmptyCommand());
        }

        return commands;
    }

    private static PgError? Collect(List<PgCommand> commands, PgCommand command)
    {
        commands.Add(command);
        return null;
    }

    private static List<(int Start, int Length)> Split(string sql, PgToken[] tokens)
    {
        List<(int Start, int Length)> statements = [];
        var start = 0;
        var depth = 0;
        var blockDepth = 0;
        var words = 0;
        var trigger = false;
        string? first = null;
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            switch (token.Kind)
            {
                case PgTokenKind.LeftParen:
                    depth++;
                    break;
                case PgTokenKind.RightParen:
                    depth--;
                    break;
                case PgTokenKind.Word:
                    var word = sql.AsSpan(token.Start, token.Length);
                    if (words == 0)
                    {
                        first = word.ToString();
                    }
                    else if (words <= 2 && first is { } leading && leading.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
                        && word.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase))
                    {
                        trigger = true;
                    }

                    words++;
                    if (trigger && (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) || word.Equals("CASE", StringComparison.OrdinalIgnoreCase)))
                    {
                        blockDepth++;
                    }
                    else if (trigger && word.Equals("END", StringComparison.OrdinalIgnoreCase))
                    {
                        blockDepth--;
                    }

                    break;
                case PgTokenKind.Semicolon when depth <= 0 && blockDepth <= 0:
                    statements.Add((start, index - start));
                    start = index + 1;
                    words = 0;
                    trigger = false;
                    first = null;
                    break;
            }
        }

        statements.Add((start, tokens.Length - start));
        return statements;
    }

    private static Result<PgCommand, PgError> ParseStatement(PgTokenEditor tokens, PgRewriteContext context)
    {
        var first = tokens.Kind(0) == PgTokenKind.Word ? tokens.Text(0).ToLowerInvariant() : string.Empty;
        switch (first)
        {
            case "begin":
                return ParseTransactionModes(tokens, 1) is { } begin ? new PgBeginCommand(begin.Mode, begin.Access) : PgError.Syntax("syntax error in BEGIN");
            case "start" when tokens.IsWord(1, "TRANSACTION"):
                return ParseTransactionModes(tokens, 2) is { } start ? new PgBeginCommand(start.Mode, start.Access) : PgError.Syntax("syntax error in START TRANSACTION");
            case "commit" or "end":
                return tokens.IsWord(1, "PREPARED") ? PgError.Unsupported("two-phase commit") : new PgCommitCommand();
            case "rollback" or "abort":
                if (tokens.IsWord(1, "PREPARED"))
                {
                    return PgError.Unsupported("two-phase commit");
                }

                for (var index = 1; index < tokens.Count; index++)
                {
                    if (tokens.IsWord(index, "TO"))
                    {
                        var name = tokens.IsWord(index + 1, "SAVEPOINT") ? index + 2 : index + 1;
                        return tokens.IsName(name) ? new PgRollbackToCommand(tokens.Text(name)) : PgError.Syntax("syntax error in ROLLBACK TO SAVEPOINT");
                    }
                }

                return new PgRollbackCommand();
            case "savepoint":
                return tokens.IsName(1) ? new PgSavepointCommand(tokens.Text(1)) : PgError.Syntax("syntax error in SAVEPOINT");
            case "release":
                var released = tokens.IsWord(1, "SAVEPOINT") ? 2 : 1;
                return tokens.IsName(released) ? new PgReleaseCommand(tokens.Text(released)) : PgError.Syntax("syntax error in RELEASE SAVEPOINT");
            case "prepare" when tokens.IsWord(1, "TRANSACTION"):
                return PgError.Unsupported("two-phase commit");
            case "prepare" or "execute":
                return PgError.Unsupported("SQL-level PREPARE and EXECUTE; use the extended query protocol");
            case "set":
                return ParseSet(tokens);
            case "reset":
                return tokens.IsWord(1, "ALL") ? new PgResetAllCommand() : new PgSetCommand(SettingName(tokens, 1, out _), null, Local: false);
            case "show":
                return ParseShow(tokens);
            case "deallocate":
                var deallocated = tokens.IsWord(1, "PREPARE") ? 2 : 1;
                return new PgDeallocateCommand(tokens.IsWord(deallocated, "ALL") ? null : tokens.IsName(deallocated) ? tokens.Identifier(deallocated) : null);
            case "discard":
                return new PgDiscardCommand();
            case "listen":
                return tokens.IsName(1) ? new PgListenCommand(tokens.Identifier(1)) : PgError.Syntax("syntax error in LISTEN");
            case "unlisten":
                return new PgUnlistenCommand(tokens.IsName(1) ? tokens.Identifier(1) : null);
            case "notify":
                return PgError.Unsupported("NOTIFY: notifications are delivered from committed changes");
            case "copy":
                return ParseCopy(tokens, context);
            case "create" when tokens.IsWord(1, "EXTENSION"):
                return new PgNoOpCommand("CREATE EXTENSION");
            case "create" when tokens.IsWord(1, "SCHEMA"):
                return new PgNoOpCommand("CREATE SCHEMA");
            case "create" when tokens.IsWord(1, "TYPE"):
                return new PgNoOpCommand("CREATE TYPE");
            case "create" when tokens.IsWord(1, "SEQUENCE"):
                return PgError.Unsupported("sequences; use SERIAL or GENERATED AS IDENTITY columns");
            case "drop" when tokens.IsWord(1, "EXTENSION"):
                return new PgNoOpCommand("DROP EXTENSION");
            case "drop" when tokens.IsWord(1, "TYPE"):
                return new PgNoOpCommand("DROP TYPE");
            case "drop" when tokens.IsWord(1, "SCHEMA"):
                return PgError.Unsupported("DROP SCHEMA");
            case "comment":
                return new PgNoOpCommand("COMMENT");
            case "vacuum" or "analyze" or "analyse":
                return new PgError("42501", $"{first.ToUpperInvariant()} is not permitted");
            case "grant" or "revoke" or "lock" or "cluster" or "checkpoint" or "load" or "security" or "reassign" or "refresh":
                return PgError.Unsupported(first.ToUpperInvariant());
            case "truncate":
                return ParseTruncate(tokens, context);
            case "table":
                tokens.Replace(0, "SELECT * FROM");
                return BuildSql(tokens, context, "SELECT");
            case "select" when CannedSelect(tokens) is { } canned:
                return canned;
            default:
                return BuildSql(tokens, context, null);
        }
    }

    private static Result<PgCommand, PgError> BuildSql(PgTokenEditor tokens, PgRewriteContext context, string? tagOverride) =>
        BuildQuery(tokens, context, tagOverride) switch
        {
            PgSqlCommand command => command,
            PgError error => error,
        };

    private static Result<PgSqlCommand, PgError> BuildQuery(PgTokenEditor tokens, PgRewriteContext context, string? tagOverride)
    {
        var verb = Verb(tokens);
        var access = Access(tokens, verb);
        var explain = tokens.IsWord(0, "EXPLAIN");
        if (explain)
        {
            var body = 1;
            while (tokens.IsAnyWord(body, "ANALYZE", "VERBOSE", "ANALYSE") || tokens.Kind(body) == PgTokenKind.LeftParen)
            {
                body = tokens.Kind(body) == PgTokenKind.LeftParen ? tokens.Match(body) + 1 : body + 1;
            }

            tokens.Replace(0, "EXPLAIN QUERY PLAN");
            tokens.RemoveRange(1, body - 1);
            access = SqlAccess.Read;
        }

        var table = verb is "INSERT" or "UPDATE" or "DELETE" ? TargetTable(tokens, verb) : null;
        var returning = verb is "INSERT" or "UPDATE" or "DELETE" ? Returning(tokens) : null;
        var tag = tagOverride ?? (verb is "CREATE" or "DROP" or "ALTER" ? DefinitionTag(tokens, verb) : null);
        return PostgresSqlRewriter.Rewrite(tokens, context) switch
        {
            PgRewrite rewrite => new PgSqlCommand(rewrite, access, verb, tag, table, returning, explain),
            PgError error => error,
        };
    }

    private static string Verb(PgTokenEditor tokens)
    {
        var first = tokens.Kind(0) == PgTokenKind.Word ? tokens.Text(0).ToUpperInvariant() : string.Empty;
        if (first != "WITH")
        {
            return first;
        }

        for (var index = 1; index < tokens.Count; index++)
        {
            if (tokens.Kind(index) == PgTokenKind.LeftParen)
            {
                index = tokens.Match(index);
                continue;
            }

            if (tokens.IsAnyWord(index, "SELECT", "INSERT", "UPDATE", "DELETE", "VALUES"))
            {
                return tokens.Text(index).ToUpperInvariant();
            }
        }

        return "SELECT";
    }

    private static SqlAccess Access(PgTokenEditor tokens, string verb)
    {
        if (verb is not ("SELECT" or "VALUES" or "EXPLAIN"))
        {
            return SqlAccess.Write;
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens.IsAnyWord(index, "INSERT", "DELETE", "REPLACE", "CREATE", "DROP", "ALTER")
                || (tokens.IsWord(index, "UPDATE") && !tokens.IsAnyWord(index - 1, "FOR", "KEY", "NO", "ON", "DO")))
            {
                return SqlAccess.Write;
            }
        }

        return SqlAccess.Read;
    }

    private static string DefinitionTag(PgTokenEditor tokens, string verb)
    {
        var index = 1;
        while (tokens.IsAnyWord(index, "OR", "REPLACE", "TEMP", "TEMPORARY", "UNIQUE", "UNLOGGED", "GLOBAL", "LOCAL"))
        {
            index++;
        }

        return tokens.Kind(index) == PgTokenKind.Word ? $"{verb} {tokens.Text(index).ToUpperInvariant()}" : verb;
    }

    private static string? TargetTable(PgTokenEditor tokens, string verb)
    {
        var index = 0;
        var keyword = verb switch
        {
            "INSERT" => "INTO",
            "DELETE" => "FROM",
            _ => "UPDATE",
        };
        while (index < tokens.Count && !tokens.IsWord(index, keyword))
        {
            index = tokens.Kind(index) == PgTokenKind.LeftParen ? tokens.Match(index) + 1 : index + 1;
        }

        index++;
        if (tokens.IsWord(index, "ONLY"))
        {
            index++;
        }

        while (tokens.IsName(index) && tokens.Kind(index + 1) == PgTokenKind.Period)
        {
            index += 2;
        }

        return tokens.IsName(index) ? tokens.Identifier(index) : null;
    }

    private static List<PgReturningItem>? Returning(PgTokenEditor tokens)
    {
        var index = 0;
        for (; index < tokens.Count; index++)
        {
            if (tokens.Kind(index) == PgTokenKind.LeftParen)
            {
                index = tokens.Match(index);
                continue;
            }

            if (tokens.IsWord(index, "RETURNING"))
            {
                break;
            }
        }

        if (index >= tokens.Count)
        {
            return null;
        }

        List<PgReturningItem> items = [];
        var start = index + 1;
        for (var cursor = start; cursor <= tokens.Count; cursor++)
        {
            if (cursor < tokens.Count && tokens.Kind(cursor) is PgTokenKind.LeftParen or PgTokenKind.LeftBracket)
            {
                cursor = tokens.Match(cursor);
                continue;
            }

            if (cursor < tokens.Count && tokens.Kind(cursor) != PgTokenKind.Comma)
            {
                continue;
            }

            items.Add(ReturningItem(tokens, start, cursor - 1));
            start = cursor + 1;
        }

        return items;
    }

    private static PgReturningItem ReturningItem(PgTokenEditor tokens, int start, int end)
    {
        if (start == end && tokens.IsOperator(start, "*"))
        {
            return new PgReturningItem(null, "*");
        }

        string? alias = null;
        var expressionEnd = end;
        if (end - start >= 2 && tokens.IsWord(end - 1, "AS") && tokens.IsName(end))
        {
            alias = tokens.Identifier(end);
            expressionEnd = end - 2;
        }
        else if (end > start && tokens.IsName(end) && tokens.Kind(end - 1) is PgTokenKind.RightParen or PgTokenKind.Word or PgTokenKind.QuotedIdentifier or PgTokenKind.Number or PgTokenKind.String)
        {
            alias = tokens.Identifier(end);
            expressionEnd = end - 1;
        }

        var column = (expressionEnd == start && tokens.IsName(start)) || (expressionEnd == start + 2 && tokens.Kind(start + 1) == PgTokenKind.Period && tokens.IsName(expressionEnd))
            ? tokens.Identifier(expressionEnd)
            : null;
        return new PgReturningItem(column, alias ?? column ?? "?column?");
    }

    private static PgCommand? CannedSelect(PgTokenEditor tokens)
    {
        var source = tokens.RenderSource(0, tokens.Count - 1);
        if (source.Contains("elemtypoid", StringComparison.Ordinal) && source.Contains("pg_type", StringComparison.Ordinal))
        {
            return new PgCannedCommand(PgTypeLoad.Types());
        }

        var call = tokens.IsWord(1, "pg_catalog") && tokens.Kind(2) == PgTokenKind.Period ? 3 : 1;
        if (tokens.IsWord(call, "pg_is_in_recovery") && tokens.Count == call + 3)
        {
            return new PgCannedCommand(new PgResult([new PgColumn("pg_is_in_recovery", PgOid.Bool)], [[SqlValue.FromIntegral(0)]], "SELECT 1"));
        }

        if (tokens.IsWord(call, "set_config") && tokens.Kind(call + 1) == PgTokenKind.LeftParen && tokens.Match(call + 1) == tokens.Count - 1
            && tokens.Kind(call + 2) == PgTokenKind.String && tokens.Kind(call + 3) == PgTokenKind.Comma
            && tokens.Kind(call + 4) == PgTokenKind.String && tokens.Kind(call + 5) == PgTokenKind.Comma)
        {
            var local = tokens.IsAnyWord(call + 6, "true", "t") || (tokens.Kind(call + 6) == PgTokenKind.String && tokens.Raw(call + 6) is "'t'" or "'true'");
            return new PgSetConfigCommand(StringValue(tokens, call + 2), StringValue(tokens, call + 4), local);
        }

        var from = 0;
        for (; from < tokens.Count && !tokens.IsWord(from, "FROM"); from++)
        {
            if (tokens.Kind(from) == PgTokenKind.LeftParen)
            {
                from = tokens.Match(from);
            }
        }

        var schema = "pg_catalog";
        var relation = from + 1;
        if (tokens.IsName(relation) && tokens.Kind(relation + 1) == PgTokenKind.Period)
        {
            schema = tokens.Identifier(relation);
            relation += 2;
        }

        return tokens.IsName(relation) && PgCatalogSql.IsAlwaysEmpty(schema, tokens.Identifier(relation))
            ? new PgCannedCommand(new PgResult([.. SelectListNames(tokens, from).Select(static name => new PgColumn(name, PgOid.Text))], [], "SELECT 0"))
            : null;
    }

    private static List<string> SelectListNames(PgTokenEditor tokens, int from)
    {
        List<string> names = [];
        var start = tokens.IsAnyWord(1, "DISTINCT", "ALL") ? 2 : 1;
        for (var index = start; index <= from; index++)
        {
            if (index < from && tokens.Kind(index) is PgTokenKind.LeftParen or PgTokenKind.LeftBracket)
            {
                index = tokens.Match(index);
                continue;
            }

            if (index < from && tokens.Kind(index) != PgTokenKind.Comma)
            {
                continue;
            }

            var end = index - 1;
            names.Add(end >= start && tokens.IsName(end) && (end == start || tokens.Kind(end - 1) is PgTokenKind.Period || tokens.IsWord(end - 1, "AS") || tokens.Kind(end - 1) is PgTokenKind.RightParen or PgTokenKind.Word)
                ? tokens.Identifier(end)
                : end >= start && tokens.IsName(start) && tokens.Kind(start + 1) == PgTokenKind.LeftParen ? tokens.Text(start).ToLowerInvariant() : "?column?");
            start = index + 1;
        }

        return names;
    }

    private static Result<PgCommand, PgError> ParseTruncate(PgTokenEditor tokens, PgRewriteContext context)
    {
        var index = 1;
        while (tokens.IsAnyWord(index, "TABLE", "ONLY"))
        {
            index++;
        }

        while (tokens.IsName(index) && tokens.Kind(index + 1) == PgTokenKind.Period)
        {
            index += 2;
        }

        if (!tokens.IsName(index))
        {
            return PgError.Syntax("syntax error in TRUNCATE");
        }

        if (tokens.Kind(index + 1) == PgTokenKind.Comma)
        {
            return PgError.Unsupported("TRUNCATE of several tables in one statement");
        }

        var sql = "DELETE FROM " + Quote(tokens.Identifier(index));
        var lexed = PgLexer.Tokenize(sql);
        return lexed switch
        {
            PgToken[] lexedTokens => BuildSql(PgTokenEditor.Create(sql, lexedTokens), context, "TRUNCATE TABLE"),
            PgError error => error,
        };
    }

    private static Result<PgCommand, PgError> ParseSet(PgTokenEditor tokens)
    {
        var index = 1;
        var local = false;
        if (tokens.IsWord(index, "LOCAL"))
        {
            local = true;
            index++;
        }
        else if (tokens.IsWord(index, "SESSION") && !tokens.IsAnyWord(index + 1, "CHARACTERISTICS", "AUTHORIZATION"))
        {
            index++;
        }

        if (tokens.IsWord(index, "SESSION") && tokens.IsWord(index + 1, "CHARACTERISTICS"))
        {
            return ParseTransactionModes(tokens, index + 4) switch
            {
                { Mode: { } mode } => new PgSetCommand("default_transaction_isolation", mode == ConsistencyMode.Strong ? "serializable" : "read committed", Local: false),
                { Access: { } access } => new PgSetCommand("default_transaction_read_only", access == SqlAccess.Read ? "on" : "off", Local: false),
                _ => PgError.Syntax("syntax error in SET SESSION CHARACTERISTICS"),
            };
        }

        if (tokens.IsWord(index, "TRANSACTION"))
        {
            return ParseTransactionModes(tokens, index + 1) is { } modes
                ? new PgSetTransactionCommand(modes.Mode, modes.Access)
                : PgError.Syntax("syntax error in SET TRANSACTION");
        }

        if (tokens.IsAnyWord(index, "ROLE", "AUTHORIZATION") || (tokens.IsWord(index, "SESSION") && tokens.IsWord(index + 1, "AUTHORIZATION")))
        {
            return new PgNoOpCommand("SET");
        }

        if (tokens.IsWord(index, "CONSTRAINTS"))
        {
            return new PgNoOpCommand("SET CONSTRAINTS");
        }

        if (tokens.IsWord(index, "TIME") && tokens.IsWord(index + 1, "ZONE"))
        {
            var zone = ValueText(tokens, index + 2);
            return new PgSetCommand("TimeZone", zone is "DEFAULT" or "LOCAL" ? null : zone, local);
        }

        if (tokens.IsWord(index, "NAMES"))
        {
            return new PgSetCommand("client_encoding", ValueText(tokens, index + 1), local);
        }

        var name = SettingName(tokens, index, out var next);
        if (name.Length == 0 || !(tokens.IsWord(next, "TO") || tokens.IsOperator(next, "=")))
        {
            return PgError.Syntax("syntax error in SET");
        }

        var value = ValueText(tokens, next + 1);
        return new PgSetCommand(name, value.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase) && tokens.IsWord(next + 1, "DEFAULT") ? null : value, local);
    }

    private static Result<PgCommand, PgError> ParseShow(PgTokenEditor tokens)
    {
        if (tokens.IsWord(1, "TIME") && tokens.IsWord(2, "ZONE"))
        {
            return new PgShowCommand("TimeZone");
        }

        if (tokens.IsWord(1, "TRANSACTION") && tokens.IsWord(2, "ISOLATION"))
        {
            return new PgShowCommand("transaction_isolation");
        }

        var name = SettingName(tokens, 1, out _);
        return name.Length == 0 ? PgError.Syntax("syntax error in SHOW") : new PgShowCommand(name);
    }

    private static string SettingName(PgTokenEditor tokens, int index, out int next)
    {
        var builder = new StringBuilder();
        next = index;
        while (tokens.IsName(next))
        {
            builder.Append(tokens.Kind(next) == PgTokenKind.QuotedIdentifier ? tokens.Identifier(next) : tokens.Text(next));
            next++;
            if (tokens.Kind(next) != PgTokenKind.Period)
            {
                break;
            }

            builder.Append('.');
            next++;
        }

        return builder.ToString();
    }

    private static string ValueText(PgTokenEditor tokens, int index)
    {
        var builder = new StringBuilder();
        for (; index < tokens.Count; index++)
        {
            switch (tokens.Kind(index))
            {
                case PgTokenKind.String or PgTokenKind.EscapeString or PgTokenKind.DollarString:
                    builder.Append(StringValue(tokens, index));
                    break;
                case PgTokenKind.QuotedIdentifier:
                    builder.Append(tokens.Identifier(index));
                    break;
                case PgTokenKind.Comma:
                    builder.Append(", ");
                    break;
                default:
                    builder.Append(tokens.Raw(index));
                    break;
            }
        }

        return builder.ToString();
    }

    private static string StringValue(PgTokenEditor tokens, int index)
    {
        var raw = tokens.Raw(index);
        return tokens.Kind(index) switch
        {
            PgTokenKind.String when raw.Length >= 2 && raw[0] == '\'' => raw[1..^1].ToString().Replace("''", "'", StringComparison.Ordinal),
            PgTokenKind.EscapeString => raw[2..^1].ToString().Replace("''", "'", StringComparison.Ordinal).Replace("\\'", "'", StringComparison.Ordinal),
            PgTokenKind.DollarString => raw[(raw[1..].IndexOf('$') + 2)..^(raw[1..].IndexOf('$') + 2)].ToString(),
            _ => raw.ToString(),
        };
    }

    private static (ConsistencyMode? Mode, SqlAccess? Access)? ParseTransactionModes(PgTokenEditor tokens, int index)
    {
        ConsistencyMode? mode = null;
        SqlAccess? access = null;
        while (index < tokens.Count)
        {
            if (tokens.Kind(index) == PgTokenKind.Comma || tokens.IsAnyWord(index, "WORK", "TRANSACTION", "DEFERRED", "IMMEDIATE", "EXCLUSIVE", "DEFERRABLE"))
            {
                index++;
            }
            else if (tokens.IsWord(index, "NOT") && tokens.IsWord(index + 1, "DEFERRABLE"))
            {
                index += 2;
            }
            else if (tokens.IsWord(index, "STRONG"))
            {
                mode = ConsistencyMode.Strong;
                index++;
            }
            else if (tokens.IsWord(index, "FAST"))
            {
                mode = ConsistencyMode.Fast;
                index++;
            }
            else if (tokens.IsWord(index, "ISOLATION") && tokens.IsWord(index + 1, "LEVEL"))
            {
                index += 2;
                if (tokens.IsWord(index, "SERIALIZABLE"))
                {
                    mode = ConsistencyMode.Strong;
                    index++;
                }
                else if ((tokens.IsWord(index, "REPEATABLE") && tokens.IsWord(index + 1, "READ"))
                    || (tokens.IsWord(index, "READ") && tokens.IsAnyWord(index + 1, "COMMITTED", "UNCOMMITTED")))
                {
                    mode = ConsistencyMode.Fast;
                    index += 2;
                }
                else
                {
                    return null;
                }
            }
            else if (tokens.IsWord(index, "READ") && tokens.IsWord(index + 1, "ONLY"))
            {
                access = SqlAccess.Read;
                index += 2;
            }
            else if (tokens.IsWord(index, "READ") && tokens.IsWord(index + 1, "WRITE"))
            {
                access = SqlAccess.Write;
                index += 2;
            }
            else
            {
                return null;
            }
        }

        return (mode, access);
    }

    private static Result<PgCommand, PgError> ParseCopy(PgTokenEditor tokens, PgRewriteContext context)
    {
        var index = 1;
        var binaryKeyword = false;
        if (tokens.IsWord(index, "BINARY"))
        {
            binaryKeyword = true;
            index++;
        }

        string? table = null;
        string? query = null;
        List<string> columns = [];
        if (tokens.Kind(index) == PgTokenKind.LeftParen)
        {
            var close = tokens.Match(index);
            query = tokens.RenderSource(index + 1, close - 1);
            index = close + 1;
        }
        else
        {
            while (tokens.IsName(index) && tokens.Kind(index + 1) == PgTokenKind.Period)
            {
                index += 2;
            }

            if (!tokens.IsName(index))
            {
                return PgError.Syntax("syntax error in COPY");
            }

            table = tokens.Identifier(index);
            index++;
            if (tokens.Kind(index) == PgTokenKind.LeftParen)
            {
                var close = tokens.Match(index);
                for (var column = index + 1; column < close; column++)
                {
                    if (tokens.IsName(column))
                    {
                        columns.Add(tokens.Identifier(column));
                    }
                }

                index = close + 1;
            }
        }

        var copyFrom = tokens.IsWord(index, "FROM");
        if (!copyFrom && !tokens.IsWord(index, "TO"))
        {
            return PgError.Syntax("syntax error in COPY: expected FROM or TO");
        }

        if (!tokens.IsAnyWord(index + 1, copyFrom ? "STDIN" : "STDOUT"))
        {
            return PgError.Unsupported("COPY with files or programs; use STDIN or STDOUT");
        }

        if (ParseCopyOptions(tokens, index + 2, binaryKeyword) is not { } options)
        {
            return PgError.Syntax("syntax error in COPY options");
        }

        if (copyFrom)
        {
            return table is null ? PgError.Syntax("COPY FROM requires a table") : new PgCopyFromCommand(table, columns, options);
        }

        var select = (query, table) switch
        {
            ({ } text, _) => text,
            (null, { } name) => $"SELECT {(columns.Count == 0 ? "*" : string.Join(", ", columns.Select(Quote)))} FROM {Quote(name)}",
            (null, null) => null,
        };
        if (select is null)
        {
            return PgError.Syntax("syntax error in COPY");
        }

        return ParseQuery(select, context) switch
        {
            PgSqlCommand command => new PgCopyToCommand(command, options),
            PgError error => error,
        };
    }

    private static PgCopyOptions? ParseCopyOptions(PgTokenEditor tokens, int index, bool binaryKeyword)
    {
        var format = binaryKeyword ? PgCopyFormat.Binary : PgCopyFormat.Text;
        char? delimiter = null;
        string? nullText = null;
        var header = false;
        char? quote = null;
        char? escape = null;
        if (tokens.IsWord(index, "WITH"))
        {
            index++;
        }

        if (tokens.Kind(index) == PgTokenKind.LeftParen)
        {
            var close = tokens.Match(index);
            for (var option = index + 1; option < close; option++)
            {
                if (tokens.Kind(option) == PgTokenKind.Comma)
                {
                    continue;
                }

                var name = tokens.Text(option).ToLowerInvariant();
                var value = option + 1 < close && tokens.Kind(option + 1) != PgTokenKind.Comma ? OptionValue(tokens, option + 1) : null;
                if (value is not null)
                {
                    option++;
                }

                switch (name)
                {
                    case "format":
                        format = value?.ToLowerInvariant() switch
                        {
                            "csv" => PgCopyFormat.Csv,
                            "binary" => PgCopyFormat.Binary,
                            "text" => PgCopyFormat.Text,
                            _ => format,
                        };
                        break;
                    case "delimiter" when value is { Length: 1 }:
                        delimiter = value[0];
                        break;
                    case "null" when value is not null:
                        nullText = value;
                        break;
                    case "header":
                        header = value is null || PgValueCodec.TryParseBool(value, out var flag) && flag || value.Equals("match", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "quote" when value is { Length: 1 }:
                        quote = value[0];
                        break;
                    case "escape" when value is { Length: 1 }:
                        escape = value[0];
                        break;
                    case "encoding" when value is not null && !value.Replace("-", string.Empty, StringComparison.Ordinal).Equals("UTF8", StringComparison.OrdinalIgnoreCase):
                        return null;
                }
            }
        }
        else
        {
            while (index < tokens.Count)
            {
                if (tokens.IsWord(index, "BINARY"))
                {
                    format = PgCopyFormat.Binary;
                }
                else if (tokens.IsWord(index, "CSV"))
                {
                    format = PgCopyFormat.Csv;
                }
                else if (tokens.IsWord(index, "HEADER"))
                {
                    header = true;
                }
                else if (tokens.IsAnyWord(index, "DELIMITER", "NULL", "QUOTE", "ESCAPE"))
                {
                    var keyword = tokens.Text(index).ToUpperInvariant();
                    index += tokens.IsWord(index + 1, "AS") ? 2 : 1;
                    var value = OptionValue(tokens, index);
                    switch (keyword)
                    {
                        case "DELIMITER" when value.Length == 1:
                            delimiter = value[0];
                            break;
                        case "NULL":
                            nullText = value;
                            break;
                        case "QUOTE" when value.Length == 1:
                            quote = value[0];
                            break;
                        case "ESCAPE" when value.Length == 1:
                            escape = value[0];
                            break;
                    }
                }
                else if (tokens.IsWord(index, "FORCE"))
                {
                    index++;
                    while (index + 1 < tokens.Count && (tokens.IsName(index + 1) || tokens.Kind(index + 1) == PgTokenKind.Comma || tokens.IsOperator(index + 1, "*")))
                    {
                        index++;
                    }
                }

                index++;
            }
        }

        var csv = format == PgCopyFormat.Csv;
        var quoteCharacter = quote ?? '"';
        return new PgCopyOptions(format, delimiter ?? (csv ? ',' : '\t'), nullText ?? (csv ? string.Empty : "\\N"), header, quoteCharacter, escape ?? quoteCharacter);
    }

    private static string OptionValue(PgTokenEditor tokens, int index) =>
        tokens.Kind(index) is PgTokenKind.String or PgTokenKind.EscapeString or PgTokenKind.DollarString
            ? StringValue(tokens, index)
            : tokens.Kind(index) == PgTokenKind.QuotedIdentifier ? tokens.Identifier(index) : tokens.Text(index);

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
