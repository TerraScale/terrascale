namespace TerraBase.Engine;

internal enum SqlTokenKind
{
    Word,
    Quoted,
    String,
    Blob,
    Number,
    Parameter,
    Semicolon,
    Dot,
    Open,
    Close,
    Comma,
    Symbol,
}

internal readonly record struct SqlToken(SqlTokenKind Kind, int Start, int Length, string Value)
{
    public int End => Start + Length;

    public bool Is(string word) => Kind == SqlTokenKind.Word && string.Equals(Value, word, StringComparison.OrdinalIgnoreCase);

    public bool IsName => Kind is SqlTokenKind.Word or SqlTokenKind.Quoted;
}

internal static class SqlText
{
    public static bool TryTokenize(string sql, out List<SqlToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(sql);
        tokens = [];
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && next == '-')
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }

            var start = i;
            SqlTokenKind kind;
            string value;
            switch (c)
            {
                case '\'' or '"' or '`':
                    if (!TryQuoted(sql, ref i, c, out value))
                    {
                        return false;
                    }

                    kind = c == '\'' ? SqlTokenKind.String : SqlTokenKind.Quoted;
                    break;
                case '[':
                    var close = sql.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        return false;
                    }

                    (kind, value, i) = (SqlTokenKind.Quoted, sql[(i + 1)..close], close + 1);
                    break;
                case 'x' or 'X' when next == '\'':
                    i++;
                    if (!TryQuoted(sql, ref i, '\'', out value))
                    {
                        return false;
                    }

                    kind = SqlTokenKind.Blob;
                    break;
                case '?':
                    i++;
                    while (i < sql.Length && char.IsAsciiDigit(sql[i]))
                    {
                        i++;
                    }

                    (kind, value) = (SqlTokenKind.Parameter, sql[start..i]);
                    break;
                case ':' or '@' or '$' when IsWordPart(next):
                    i++;
                    while (i < sql.Length && IsWordPart(sql[i]))
                    {
                        i++;
                    }

                    (kind, value) = (SqlTokenKind.Parameter, sql[start..i]);
                    break;
                case '.' when !char.IsAsciiDigit(next):
                    (kind, value, i) = (SqlTokenKind.Dot, ".", i + 1);
                    break;
                case var digit when char.IsAsciiDigit(digit) || digit == '.':
                    i++;
                    while (i < sql.Length && (IsWordPart(sql[i]) || sql[i] == '.' || (sql[i] is '+' or '-' && sql[i - 1] is 'e' or 'E')))
                    {
                        i++;
                    }

                    (kind, value) = (SqlTokenKind.Number, sql[start..i]);
                    break;
                case var letter when IsWordStart(letter):
                    while (i < sql.Length && IsWordPart(sql[i]))
                    {
                        i++;
                    }

                    (kind, value) = (SqlTokenKind.Word, sql[start..i]);
                    break;
                default:
                    kind = c switch
                    {
                        ';' => SqlTokenKind.Semicolon,
                        '(' => SqlTokenKind.Open,
                        ')' => SqlTokenKind.Close,
                        ',' => SqlTokenKind.Comma,
                        _ => SqlTokenKind.Symbol,
                    };
                    (value, i) = (sql[i..(i + 1)], i + 1);
                    break;
            }

            tokens.Add(new SqlToken(kind, start, i - start, value));
        }

        return true;
    }

    public static List<List<SqlToken>> Statements(List<SqlToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var statements = new List<List<SqlToken>>();
        var current = new List<SqlToken>();
        var body = 0;
        var bodyDone = false;
        foreach (var token in tokens)
        {
            if (token.Kind == SqlTokenKind.Semicolon && body == 0)
            {
                if (current.Count > 0)
                {
                    statements.Add(current);
                    current = [];
                }

                bodyDone = false;
                continue;
            }

            current.Add(token);
            if (token.Kind != SqlTokenKind.Word || bodyDone || !IsTrigger(current))
            {
                continue;
            }

            if (body == 0 && token.Is("BEGIN"))
            {
                body = 1;
            }
            else if (body > 0 && token.Is("CASE"))
            {
                body++;
            }
            else if (body > 0 && token.Is("END") && --body == 0)
            {
                bodyDone = true;
            }
        }

        if (current.Count > 0)
        {
            statements.Add(current);
        }

        return statements;
    }

    public static string Text(string sql, List<SqlToken> statement)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(statement);
        return statement.Count == 0 ? "" : sql[statement[0].Start..statement[^1].End];
    }

    public static bool IsOneStatement(string sql) => TryTokenize(sql, out var tokens) && Statements(tokens).Count == 1;

    private static bool IsTrigger(List<SqlToken> statement) =>
        statement.Count > 2 && statement[0].Is("CREATE")
        && (statement[1].Is("TRIGGER") || (statement.Count > 3 && (statement[1].Is("TEMP") || statement[1].Is("TEMPORARY")) && statement[2].Is("TRIGGER")));

    private static bool TryQuoted(string sql, ref int i, char quote, out string value)
    {
        var text = new System.Text.StringBuilder();
        for (var j = i + 1; j < sql.Length; j++)
        {
            if (sql[j] != quote)
            {
                text.Append(sql[j]);
                continue;
            }

            if (j + 1 < sql.Length && sql[j + 1] == quote)
            {
                text.Append(quote);
                j++;
                continue;
            }

            i = j + 1;
            value = text.ToString();
            return true;
        }

        value = "";
        return false;
    }

    private static bool IsWordStart(char c) => char.IsAsciiLetter(c) || c == '_' || c >= 0x80;

    private static bool IsWordPart(char c) => IsWordStart(c) || char.IsAsciiDigit(c) || c == '$';
}
