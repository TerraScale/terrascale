using System.Text;

namespace TerraBase.Features.Import;

/// <summary>One token of a DDL statement.</summary>
/// <param name="Kind">Token kind.</param>
/// <param name="Text">Identifier (unquoted text, original case), string contents, number or punctuation.</param>
internal readonly record struct SqlToken(SqlTokenKind Kind, string Text)
{
    /// <summary>Whether this is the unquoted keyword <paramref name="keyword"/> (case-insensitive).</summary>
    public bool Is(string keyword) => Kind == SqlTokenKind.Word && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this is the punctuation <paramref name="symbol"/>.</summary>
    public bool IsSymbol(string symbol) => Kind == SqlTokenKind.Symbol && Text == symbol;

    /// <summary>Whether this names something: an unquoted word or a quoted identifier.</summary>
    public bool IsName => Kind is SqlTokenKind.Word or SqlTokenKind.Quoted;
}

internal enum SqlTokenKind
{
    Word,
    Quoted,
    String,
    Number,
    Symbol,
}

/// <summary>
/// Tokenizes one complete DDL statement for reading, never for running it: words, quoted identifiers (<c>"x"</c>, and for
/// SQLite <c>[x]</c> and <c>`x`</c>), strings (<c>'x'</c>, <c>E'x'</c>, dollar quotes), numbers and punctuation; comments are
/// dropped. Malformed input ends the token list early, which the readers treat as a statement they cannot read.
/// </summary>
internal static class SqlTokens
{
    /// <summary>At most this many tokens per statement: a table of 1600 columns stays far below it.</summary>
    public const int MaxTokens = 200_000;

    public static List<SqlToken> Read(string sql, bool sqlite)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        while (i < sql.Length && tokens.Count < MaxTokens)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && At(sql, i + 1, '-'))
            {
                i = sql.IndexOf('\n', i) is var end and >= 0 ? end + 1 : sql.Length;
            }
            else if (c == '/' && At(sql, i + 1, '*'))
            {
                i = SkipBlockComment(sql, i);
            }
            else if (c == '"' || (sqlite && c == '`'))
            {
                if (Quoted(sql, ref i, c, c) is not { } name)
                {
                    break;
                }

                tokens.Add(new(SqlTokenKind.Quoted, name));
            }
            else if (sqlite && c == '[')
            {
                var end = sql.IndexOf(']', i + 1);
                if (end < 0)
                {
                    break;
                }

                tokens.Add(new(SqlTokenKind.Quoted, sql[(i + 1)..end]));
                i = end + 1;
            }
            else if (c == '\'' || ((c is 'E' or 'e') && At(sql, i + 1, '\'') && !sqlite))
            {
                var escapes = c != '\'';
                i += escapes ? 1 : 0;
                if ((escapes ? EscapeString(sql, ref i) : Quoted(sql, ref i, '\'', '\'')) is not { } text)
                {
                    break;
                }

                tokens.Add(new(SqlTokenKind.String, text));
            }
            else if (c == '$' && !sqlite && DollarTag(sql, i) is { } tag)
            {
                var end = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    break;
                }

                tokens.Add(new(SqlTokenKind.String, sql[(i + tag.Length)..end]));
                i = end + tag.Length;
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1])))
            {
                var start = i;
                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '.'
                    || ((sql[i] is '+' or '-') && sql[i - 1] is 'e' or 'E')))
                {
                    i++;
                }

                tokens.Add(new(SqlTokenKind.Number, sql[start..i]));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$'))
                {
                    i++;
                }

                tokens.Add(new(SqlTokenKind.Word, sql[start..i]));
            }
            else if (c == ':' && At(sql, i + 1, ':'))
            {
                tokens.Add(new(SqlTokenKind.Symbol, "::"));
                i += 2;
            }
            else
            {
                tokens.Add(new(SqlTokenKind.Symbol, c.ToString()));
                i++;
            }
        }

        return tokens;
    }

    /// <summary>Splits <paramref name="tokens"/> between <paramref name="start"/> and <paramref name="end"/> at commas outside parentheses.</summary>
    public static List<List<SqlToken>> SplitTopLevel(List<SqlToken> tokens, int start, int end)
    {
        var parts = new List<List<SqlToken>>();
        var current = new List<SqlToken>();
        var depth = 0;
        for (var i = start; i < end; i++)
        {
            var token = tokens[i];
            if (token.IsSymbol("(") || token.IsSymbol("["))
            {
                depth++;
            }
            else if (token.IsSymbol(")") || token.IsSymbol("]"))
            {
                depth--;
            }

            if (depth == 0 && token.IsSymbol(","))
            {
                parts.Add(current);
                current = [];
            }
            else
            {
                current.Add(token);
            }
        }

        if (current.Count > 0)
        {
            parts.Add(current);
        }

        return parts;
    }

    /// <summary>The index of the parenthesis closing the one at <paramref name="open"/>, or -1.</summary>
    public static int Closing(List<SqlToken> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].IsSymbol("("))
            {
                depth++;
            }
            else if (tokens[i].IsSymbol(")") && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Reads a possibly qualified name (<c>a</c>, <c>a.b</c>) at <paramref name="i"/>: its parts, unquoted words as written.</summary>
    public static List<string>? QualifiedName(List<SqlToken> tokens, ref int i)
    {
        if (i >= tokens.Count || !tokens[i].IsName)
        {
            return null;
        }

        var parts = new List<string> { tokens[i++].Text };
        while (i + 1 < tokens.Count && tokens[i].IsSymbol(".") && tokens[i + 1].IsName)
        {
            parts.Add(tokens[i + 1].Text);
            i += 2;
        }

        return parts;
    }

    /// <summary>The names of a parenthesized list at <paramref name="open"/>, or null when it holds anything else.</summary>
    public static List<string>? NameList(List<SqlToken> tokens, int open, out int close)
    {
        close = open < tokens.Count && tokens[open].IsSymbol("(") ? Closing(tokens, open) : -1;
        if (close < 0)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var part in SplitTopLevel(tokens, open + 1, close))
        {
            // SQLite allows "col COLLATE x ASC" in key lists; the name comes first.
            if (part.Count == 0 || !part[0].IsName)
            {
                return null;
            }

            names.Add(part[0].Text);
        }

        return names;
    }

    /// <summary>The tokens as text: quoted identifiers stay quoted, words keep their case.</summary>
    public static string Text(IEnumerable<SqlToken> tokens)
    {
        var text = new StringBuilder();
        SqlToken? previous = null;
        foreach (var token in tokens)
        {
            if (previous is { } last && !last.IsSymbol("(") && !last.IsSymbol(".") && !token.IsSymbol("(") && !token.IsSymbol(")")
                && !token.IsSymbol(",") && !token.IsSymbol(".") && !token.IsSymbol("[") && !token.IsSymbol("]") && !last.IsSymbol("["))
            {
                text.Append(' ');
            }

            text.Append(token.Kind switch
            {
                SqlTokenKind.Quoted => ImportNames.Quote(token.Text),
                SqlTokenKind.String => "'" + token.Text.Replace("'", "''", StringComparison.Ordinal) + "'",
                _ => token.Text,
            });
            previous = token;
        }

        return text.ToString();
    }

    private static bool At(string sql, int i, char c) => i < sql.Length && sql[i] == c;

    private static int SkipBlockComment(string sql, int i)
    {
        // PostgreSQL block comments nest.
        var depth = 0;
        while (i < sql.Length)
        {
            if (sql[i] == '/' && At(sql, i + 1, '*'))
            {
                depth++;
                i += 2;
            }
            else if (sql[i] == '*' && At(sql, i + 1, '/'))
            {
                i += 2;
                if (--depth == 0)
                {
                    return i;
                }
            }
            else
            {
                i++;
            }
        }

        return i;
    }

    private static string? Quoted(string sql, ref int i, char open, char close)
    {
        var text = new StringBuilder();
        for (var j = i + 1; j < sql.Length; j++)
        {
            if (sql[j] != close)
            {
                text.Append(sql[j]);
            }
            else if (At(sql, j + 1, close))
            {
                text.Append(close);
                j++;
            }
            else
            {
                i = j + 1;
                return text.ToString();
            }
        }

        _ = open;
        return null;
    }

    private static string? EscapeString(string sql, ref int i)
    {
        var text = new StringBuilder();
        for (var j = i + 1; j < sql.Length; j++)
        {
            if (sql[j] == '\\' && j + 1 < sql.Length)
            {
                text.Append(sql[++j]);
            }
            else if (sql[j] == '\'' && At(sql, j + 1, '\''))
            {
                text.Append('\'');
                j++;
            }
            else if (sql[j] == '\'')
            {
                i = j + 1;
                return text.ToString();
            }
            else
            {
                text.Append(sql[j]);
            }
        }

        return null;
    }

    /// <summary>The dollar-quote tag (<c>$$</c> or <c>$tag$</c>) starting at <paramref name="i"/>, or null.</summary>
    public static string? DollarTag(string sql, int i)
    {
        var j = i + 1;
        while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_'))
        {
            j++;
        }

        return j < sql.Length && sql[j] == '$' && (j == i + 1 || !char.IsAsciiDigit(sql[i + 1])) ? sql[i..(j + 1)] : null;
    }
}
