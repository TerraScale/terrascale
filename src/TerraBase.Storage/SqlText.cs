using System.Text;

namespace TerraBase.Storage;

internal enum TokenKind
{
    Word,
    QuotedIdentifier,
    Literal,
    Parameter,
    Symbol,
}

internal readonly record struct SqlToken(TokenKind Kind, int Start, int Length, string Text)
{
    public int End => Start + Length;

    public bool IsIdentifier => Kind is TokenKind.Word or TokenKind.QuotedIdentifier;

    public bool Is(string keyword) => Kind == TokenKind.Word && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

    public bool IsSymbol(string symbol) => Kind == TokenKind.Symbol && string.Equals(Text, symbol, StringComparison.Ordinal);

    public bool Names(string identifier) => IsIdentifier && string.Equals(Text, identifier, StringComparison.OrdinalIgnoreCase);
}

internal static class SqlTokenizer
{
    private static readonly string[] MultiSymbols = ["->>", "||", "<=", ">=", "<>", "!=", "==", "<<", ">>", "->"];

    public static List<SqlToken> Tokenize(string sql)
    {
        List<SqlToken> tokens = [];
        var index = 0;
        while (index < sql.Length)
        {
            var current = sql[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '-' && Next(sql, index) == '-')
            {
                var end = sql.IndexOf('\n', index);
                index = end < 0 ? sql.Length : end + 1;
                continue;
            }

            if (current == '/' && Next(sql, index) == '*')
            {
                var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? sql.Length : end + 2;
                continue;
            }

            var start = index;
            switch (current)
            {
                case '\'':
                    index = Quoted(sql, index, '\'');
                    tokens.Add(new SqlToken(TokenKind.Literal, start, index - start, sql[start..index]));
                    continue;
                case '"' or '`':
                    index = Quoted(sql, index, current);
                    tokens.Add(new SqlToken(TokenKind.QuotedIdentifier, start, index - start, Unquote(sql[start..index])));
                    continue;
                case '[':
                    var close = sql.IndexOf(']', index + 1);
                    index = close < 0 ? sql.Length : close + 1;
                    tokens.Add(new SqlToken(TokenKind.QuotedIdentifier, start, index - start, sql[(start + 1)..Math.Max(start + 1, index - 1)]));
                    continue;
                case '?' or ':' or '@' or '$':
                    index++;
                    while (index < sql.Length && (char.IsAsciiLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
                    {
                        index++;
                    }

                    tokens.Add(new SqlToken(TokenKind.Parameter, start, index - start, sql[start..index]));
                    continue;
            }

            if ((current is 'x' or 'X') && Next(sql, index) == '\'')
            {
                index = Quoted(sql, index + 1, '\'');
                tokens.Add(new SqlToken(TokenKind.Literal, start, index - start, sql[start..index]));
                continue;
            }

            if (char.IsAsciiDigit(current) || (current == '.' && char.IsAsciiDigit(Next(sql, index))))
            {
                index++;
                while (index < sql.Length && (char.IsAsciiLetterOrDigit(sql[index]) || sql[index] is '.' or '_'
                    || ((sql[index] is '+' or '-') && sql[index - 1] is 'e' or 'E')))
                {
                    index++;
                }

                tokens.Add(new SqlToken(TokenKind.Literal, start, index - start, sql[start..index]));
                continue;
            }

            if (char.IsLetter(current) || current == '_' || current > 127)
            {
                index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$' || sql[index] > 127))
                {
                    index++;
                }

                tokens.Add(new SqlToken(TokenKind.Word, start, index - start, sql[start..index]));
                continue;
            }

            var symbol = current.ToString();
            foreach (var candidate in MultiSymbols)
            {
                if (string.CompareOrdinal(sql, index, candidate, 0, candidate.Length) == 0)
                {
                    symbol = candidate;
                    break;
                }
            }

            index += symbol.Length;
            tokens.Add(new SqlToken(TokenKind.Symbol, start, symbol.Length, symbol));
        }

        return tokens;
    }

    private static char Next(string sql, int index) => index + 1 < sql.Length ? sql[index + 1] : '\0';

    private static int Quoted(string sql, int index, char quote)
    {
        index++;
        while (index < sql.Length)
        {
            if (sql[index] == quote)
            {
                if (index + 1 < sql.Length && sql[index + 1] == quote)
                {
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            index++;
        }

        return sql.Length;
    }

    private static string Unquote(string quoted)
    {
        if (quoted.Length < 2)
        {
            return quoted;
        }

        var quote = quoted[0];
        var inner = quoted[1..^1];
        return inner.Replace(string.Concat(quote.ToString(), quote.ToString()), quote.ToString(), StringComparison.Ordinal);
    }
}

internal static class SqlAnalyzer
{
    private const string SnowflakeCall = "_tb_snowflake()";

    public static int MainVerb(List<SqlToken> tokens)
    {
        if (tokens.Count == 0)
        {
            return -1;
        }

        if (!tokens[0].Is("WITH"))
        {
            return 0;
        }

        var depth = 0;
        for (var index = 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            depth += token.IsSymbol("(") ? 1 : token.IsSymbol(")") ? -1 : 0;
            if (depth == 0 && (token.Is("INSERT") || token.Is("REPLACE") || token.Is("UPDATE") || token.Is("DELETE") || token.Is("SELECT")))
            {
                return index;
            }
        }

        return -1;
    }

    public static StatementAnnotation? Annotate(List<SqlToken> tokens, Func<string, TableSchema?> schemas)
    {
        var verb = MainVerb(tokens);
        if (verb < 0)
        {
            return null;
        }

        int tableIndex;
        int setIndex;
        if (tokens[verb].Is("UPDATE"))
        {
            tableIndex = SkipConflictClause(tokens, verb + 1);
            setIndex = FindTopLevel(tokens, tableIndex, static (list, index) => list[index].Is("SET"));
        }
        else if (tokens[verb].Is("INSERT") || tokens[verb].Is("REPLACE"))
        {
            tableIndex = FindTopLevel(tokens, verb, static (list, index) => list[index].Is("INTO")) + 1;
            var doUpdate = FindTopLevel(tokens, tableIndex, static (list, index) =>
                list[index].Is("DO") && index + 2 < list.Count && list[index + 1].Is("UPDATE") && list[index + 2].Is("SET"));
            setIndex = doUpdate < 0 ? -1 : doUpdate + 2;
        }
        else
        {
            return null;
        }

        if (tableIndex <= 0 || setIndex < 0 || TableName(tokens, tableIndex) is not { } table || schemas(table) is not { HasDeltaColumns: true } schema)
        {
            return null;
        }

        HashSet<string> absolute = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (start, end) in Split(tokens, setIndex + 1, static token =>
            token.Is("FROM") || token.Is("WHERE") || token.Is("RETURNING") || token.Is("ORDER") || token.Is("LIMIT") || token.Is("ON") || token.IsSymbol(";")))
        {
            if (end - start < 3)
            {
                continue;
            }

            if (tokens[start].IsSymbol("("))
            {
                for (var index = start + 1; index < end && !tokens[index].IsSymbol(")"); index++)
                {
                    if (tokens[index].IsIdentifier && schema.FindColumn(tokens[index].Text) is { IsDeltaCounter: true } tupleColumn)
                    {
                        absolute.Add(tupleColumn.Name);
                    }
                }

                continue;
            }

            if (!tokens[start].IsIdentifier || !tokens[start + 1].IsSymbol("=") || schema.FindColumn(tokens[start].Text) is not { IsDeltaCounter: true } column)
            {
                continue;
            }

            if (!IsRelative(tokens, start + 2, end, column.Name))
            {
                absolute.Add(column.Name);
            }
        }

        return absolute.Count == 0 ? null : new StatementAnnotation(schema.Table, absolute);
    }

    public static string? RewriteSnowflakeInsert(string sql, List<SqlToken> tokens, Func<string, TableSchema?> schemas)
    {
        var verb = MainVerb(tokens);
        if (verb < 0 || !(tokens[verb].Is("INSERT") || tokens[verb].Is("REPLACE")))
        {
            return null;
        }

        var into = FindTopLevel(tokens, verb, static (list, index) => list[index].Is("INTO"));
        if (into < 0 || TableName(tokens, into + 1) is not { } table || schemas(table) is not { IntegerPrimaryKey: { } key } schema)
        {
            return null;
        }

        var cursor = into + 2;
        if (cursor < tokens.Count && tokens[cursor].IsSymbol("."))
        {
            cursor += 2;
        }

        if (cursor < tokens.Count && tokens[cursor].Is("AS"))
        {
            cursor += 2;
        }

        List<(int Position, string Text)> edits = [];
        var keyPosition = -1;
        var hasColumnList = cursor < tokens.Count && tokens[cursor].IsSymbol("(");
        if (hasColumnList)
        {
            var close = Matching(tokens, cursor);
            if (close < 0)
            {
                return null;
            }

            var position = 0;
            for (var index = cursor + 1; index < close; index++)
            {
                if (tokens[index].IsSymbol(","))
                {
                    position++;
                }
                else if (tokens[index].Names(key.Name) && keyPosition < 0)
                {
                    keyPosition = position;
                }
            }

            if (keyPosition < 0)
            {
                edits.Add((tokens[cursor].End, string.Concat(TableSql.Quote(key.Name), ", ")));
            }

            cursor = close + 1;
        }
        else
        {
            for (var index = 0; index < schema.InsertableColumns.Count; index++)
            {
                if (ReferenceEquals(schema.InsertableColumns[index], key))
                {
                    keyPosition = index;
                }
            }
        }

        if (cursor >= tokens.Count)
        {
            return null;
        }

        if (tokens[cursor].Is("DEFAULT") && cursor + 1 < tokens.Count && tokens[cursor + 1].Is("VALUES"))
        {
            if (hasColumnList)
            {
                return null;
            }

            return string.Concat(
                sql.AsSpan(0, tokens[cursor].Start),
                string.Concat("(", TableSql.Quote(key.Name), ") VALUES (", SnowflakeCall, ")"),
                sql.AsSpan(tokens[cursor + 1].End));
        }

        if (tokens[cursor].Is("VALUES"))
        {
            var row = cursor + 1;
            while (row < tokens.Count && tokens[row].IsSymbol("("))
            {
                var close = Matching(tokens, row);
                if (close < 0)
                {
                    return null;
                }

                if (hasColumnList && keyPosition < 0)
                {
                    edits.Add((tokens[row].End, string.Concat(SnowflakeCall, ", ")));
                }
                else if (Slot(tokens, row + 1, close, keyPosition) is var (first, last) && first >= 0)
                {
                    edits.Add((tokens[first].Start, "coalesce("));
                    edits.Add((tokens[last].End, string.Concat(", ", SnowflakeCall, ")")));
                }

                row = close + 1;
                if (row < tokens.Count && tokens[row].IsSymbol(","))
                {
                    row++;
                    continue;
                }

                break;
            }
        }
        else if ((tokens[cursor].Is("SELECT") || tokens[cursor].Is("WITH")) && hasColumnList && keyPosition < 0)
        {
            var end = FindTopLevel(tokens, cursor, static (list, index) =>
                list[index].Is("RETURNING") || list[index].IsSymbol(";") || (list[index].Is("ON") && index + 1 < list.Count && list[index + 1].Is("CONFLICT")));
            var stop = end < 0 ? tokens[^1].End : tokens[end - 1].End;
            edits.Add((tokens[cursor].Start, string.Concat("SELECT ", SnowflakeCall, ", * FROM (")));
            edits.Add((stop, ") WHERE true"));
        }
        else
        {
            return null;
        }

        if (edits.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder(sql);
        foreach (var (position, text) in edits.OrderByDescending(static edit => edit.Position))
        {
            builder.Insert(position, text);
        }

        return builder.ToString();
    }

    private static bool IsRelative(List<SqlToken> tokens, int start, int end, string column)
    {
        var length = end - start;
        if (length >= 2 && tokens[start].Names(column) && (tokens[start + 1].IsSymbol("+") || tokens[start + 1].IsSymbol("-")))
        {
            return true;
        }

        if (length >= 4 && tokens[start].IsIdentifier && !tokens[start].Names("excluded") && tokens[start + 1].IsSymbol(".")
            && tokens[start + 2].Names(column) && (tokens[start + 3].IsSymbol("+") || tokens[start + 3].IsSymbol("-")))
        {
            return true;
        }

        if (length >= 2 && tokens[end - 1].Names(column))
        {
            var before = end - 2;
            if (before >= start && tokens[before].IsSymbol("."))
            {
                if (before - 1 < start || tokens[before - 1].Names("excluded"))
                {
                    return false;
                }

                before -= 2;
            }

            return before >= start && tokens[before].IsSymbol("+");
        }

        return false;
    }

    private static (int First, int Last) Slot(List<SqlToken> tokens, int start, int end, int position)
    {
        var current = 0;
        var depth = 0;
        var first = -1;
        for (var index = start; index < end; index++)
        {
            var token = tokens[index];
            if (depth == 0 && token.IsSymbol(","))
            {
                if (current == position && first >= 0)
                {
                    return (first, index - 1);
                }

                current++;
                continue;
            }

            depth += token.IsSymbol("(") ? 1 : token.IsSymbol(")") ? -1 : 0;
            if (current == position && first < 0)
            {
                first = index;
            }
        }

        return current == position && first >= 0 ? (first, end - 1) : (-1, -1);
    }

    private static List<(int Start, int End)> Split(List<SqlToken> tokens, int start, Func<SqlToken, bool> terminator)
    {
        List<(int Start, int End)> parts = [];
        var depth = 0;
        var partStart = start;
        var index = start;
        for (; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (depth == 0 && terminator(token))
            {
                break;
            }

            if (depth == 0 && token.IsSymbol(","))
            {
                parts.Add((partStart, index));
                partStart = index + 1;
                continue;
            }

            depth += token.IsSymbol("(") ? 1 : token.IsSymbol(")") ? -1 : 0;
        }

        if (index > partStart)
        {
            parts.Add((partStart, index));
        }

        return parts;
    }

    private static int FindTopLevel(List<SqlToken> tokens, int start, Func<List<SqlToken>, int, bool> predicate)
    {
        var depth = 0;
        for (var index = Math.Max(start, 0); index < tokens.Count; index++)
        {
            if (depth == 0 && predicate(tokens, index))
            {
                return index;
            }

            depth += tokens[index].IsSymbol("(") ? 1 : tokens[index].IsSymbol(")") ? -1 : 0;
        }

        return -1;
    }

    private static int Matching(List<SqlToken> tokens, int open)
    {
        var depth = 0;
        for (var index = open; index < tokens.Count; index++)
        {
            depth += tokens[index].IsSymbol("(") ? 1 : tokens[index].IsSymbol(")") ? -1 : 0;
            if (depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int SkipConflictClause(List<SqlToken> tokens, int index) =>
        index + 1 < tokens.Count && tokens[index].Is("OR") ? index + 2 : index;

    private static string? TableName(List<SqlToken> tokens, int index)
    {
        if (index < 0 || index >= tokens.Count || !tokens[index].IsIdentifier)
        {
            return null;
        }

        if (index + 2 < tokens.Count && tokens[index + 1].IsSymbol(".") && tokens[index + 2].IsIdentifier)
        {
            return string.Equals(tokens[index].Text, "main", StringComparison.OrdinalIgnoreCase) ? tokens[index + 2].Text : null;
        }

        return tokens[index].Text;
    }
}
