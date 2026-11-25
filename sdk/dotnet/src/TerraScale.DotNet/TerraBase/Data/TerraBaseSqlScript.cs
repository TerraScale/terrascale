using System.Net;

namespace TerraScale.DotNet.TerraBase;

internal readonly record struct TerraBaseSqlFrame(string Kind, bool Wrapped)
{
    private static readonly TerraBaseSqlStatementResult Acknowledged = new([], [], 0);

    public IReadOnlyList<TerraBaseSqlStatementResult> Results(IReadOnlyList<TerraBaseSqlStatementBody>? bodies, int statements, HttpStatusCode? status)
    {
        var expected = Kind != "execute" ? 0 : Wrapped ? statements - 2 : statements;
        if (bodies is null || bodies.Count != expected)
        {
            throw new TerraBaseException(status, "TerraBase returned a result count that does not match the statements.", "invalid_response", null);
        }

        IReadOnlyList<TerraBaseSqlStatementResult> results = [.. bodies.Select(static body => body.ToResult())];
        return Kind != "execute" ? [.. Enumerable.Repeat(Acknowledged, statements)]
            : Wrapped ? [Acknowledged, .. results, Acknowledged]
            : results;
    }
}

internal sealed record TerraBaseScriptStatement(string Sql, IReadOnlyList<string> Named, int Positional, bool Numbered);

internal static class TerraBaseSqlScript
{
    private enum Token
    {
        None,
        Word,
        Semicolon,
        Other,
    }

    public static TerraBaseSqlFrame Frame(IReadOnlyList<TerraBaseSqlStatement> statements)
    {
        string?[] controls = [.. statements.Select(static statement => TransactionControl(statement.Sql))];
        var wrapped = controls is ["begin", .., "commit"] && controls.Length >= 2;
        var inner = wrapped ? controls[1..^1] : controls;
        return new(inner is [{ } control] ? control : "execute", wrapped);
    }

    private static string? TransactionControl(string sql)
    {
        var tokens = Tokens(sql);
        return tokens switch
        {
            ["BEGIN" or "START", ..] => "begin",
            ["COMMIT" or "END", ..] => "commit",
            ["ROLLBACK", ..] when !tokens.Contains("TO") => "rollback",
            _ => null,
        };
    }

    private static List<string?> Tokens(string sql)
    {
        List<string?> tokens = [];
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

            if (c is '\'' or '"' or '`' || c is 'x' or 'X' && next == '\'')
            {
                var quote = c is 'x' or 'X' ? next : c;
                i = Unquote(sql, c is 'x' or 'X' ? i + 1 : i, quote);
                tokens.Add(null);
                continue;
            }

            if (c == '[')
            {
                var close = sql.IndexOf(']', i + 1);
                i = close < 0 ? sql.Length : close + 1;
                tokens.Add(null);
                continue;
            }

            if (c == '?')
            {
                i++;
                while (i < sql.Length && char.IsAsciiDigit(sql[i]))
                {
                    i++;
                }

                tokens.Add(null);
                continue;
            }

            if (c is ':' or '@' or '$' && WordPart(next))
            {
                i++;
                while (i < sql.Length && WordPart(sql[i]))
                {
                    i++;
                }

                tokens.Add(null);
                continue;
            }

            if (char.IsAsciiDigit(c) || c == '.' && char.IsAsciiDigit(next))
            {
                i++;
                while (i < sql.Length && (WordPart(sql[i]) || sql[i] == '.' || sql[i] is '+' or '-' && sql[i - 1] is 'e' or 'E'))
                {
                    i++;
                }

                tokens.Add(null);
                continue;
            }

            if (WordStart(c))
            {
                var start = i;
                while (i < sql.Length && WordPart(sql[i]))
                {
                    i++;
                }

                tokens.Add(sql[start..i].ToUpperInvariant());
                continue;
            }

            tokens.Add(null);
            i++;
        }

        return tokens;
    }

    private static int Unquote(string sql, int open, char quote)
    {
        for (var j = open + 1; j < sql.Length; j++)
        {
            if (sql[j] != quote)
            {
                continue;
            }

            if (j + 1 < sql.Length && sql[j + 1] == quote)
            {
                j++;
                continue;
            }

            return j + 1;
        }

        return sql.Length;
    }

    private static bool WordStart(char c) => char.IsAsciiLetter(c) || c == '_' || c >= 0x80;

    private static bool WordPart(char c) => WordStart(c) || char.IsAsciiDigit(c) || c == '$';

    public static IReadOnlyList<TerraBaseScriptStatement> Split(string sql)
    {
        List<TerraBaseScriptStatement> statements = [];
        List<string> named = [];
        var positional = 0;
        var numbered = false;
        var start = 0;
        var significant = false;
        var words = 0;
        var trigger = false;
        string? firstWord = null;
        var last = Token.None;
        var beforeLast = Token.None;
        var lastWasEnd = false;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }

            if (c == ';')
            {
                if (!trigger || (lastWasEnd && beforeLast == Token.Semicolon))
                {
                    if (significant)
                    {
                        statements.Add(new(sql[start..i].Trim(), [.. named], positional, numbered));
                    }

                    named.Clear();
                    positional = 0;
                    numbered = false;
                    significant = false;
                    words = 0;
                    trigger = false;
                    firstWord = null;
                    last = Token.None;
                    beforeLast = Token.None;
                    lastWasEnd = false;
                    start = i + 1;
                    i++;
                    continue;
                }

                Shift(Token.Semicolon, false);
                i++;
                continue;
            }

            significant = true;
            switch (c)
            {
                case '\'' or '"' or '`':
                    i = Quoted(sql, i, c);
                    Shift(Token.Other, false);
                    continue;
                case '[':
                    var close = sql.IndexOf(']', i + 1);
                    i = close < 0 ? sql.Length : close + 1;
                    Shift(Token.Other, false);
                    continue;
                case '?':
                    i++;
                    var digits = i;
                    while (i < sql.Length && char.IsAsciiDigit(sql[i]))
                    {
                        i++;
                    }

                    if (i > digits)
                    {
                        numbered = true;
                    }
                    else
                    {
                        positional++;
                    }

                    Shift(Token.Other, false);
                    continue;
                case ':' or '@' or '$' when i + 1 < sql.Length && IsNameChar(sql[i + 1]):
                    var name = i + 1;
                    i = name;
                    while (i < sql.Length && IsNameChar(sql[i]))
                    {
                        i++;
                    }

                    named.Add(sql[name..i]);
                    Shift(Token.Other, false);
                    continue;
            }

            if (IsNameChar(c) && !char.IsAsciiDigit(c))
            {
                var begin = i;
                while (i < sql.Length && (IsNameChar(sql[i]) || sql[i] == '$'))
                {
                    i++;
                }

                var word = sql.AsSpan(begin, i - begin);
                words++;
                if (words == 1)
                {
                    firstWord = word.ToString();
                }
                else if (!trigger && words <= 3 && firstWord is not null && firstWord.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
                    && word.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase))
                {
                    trigger = true;
                }

                Shift(Token.Word, word.Equals("END", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            i++;
            Shift(Token.Other, false);
        }

        if (significant)
        {
            statements.Add(new(sql[start..].Trim(), [.. named], positional, numbered));
        }

        return statements;

        void Shift(Token token, bool end)
        {
            beforeLast = last;
            last = token;
            lastWasEnd = end;
        }
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c > 0x7F;

    private static int Quoted(string sql, int i, char quote)
    {
        i++;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }
}
