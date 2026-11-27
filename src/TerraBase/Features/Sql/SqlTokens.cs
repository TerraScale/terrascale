using System.Text;

namespace TerraBase.Features.Sql;

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
        var span = sql.AsSpan();
        var length = span.Length;
        tokens = new List<SqlToken>(Math.Clamp(length / 4, 4, 64));
        var i = 0;
        while (i < length)
        {
            var c = span[i];
            var next = i + 1 < length ? span[i + 1] : '\0';
            if (StartsSpace(c))
            {
                i++;
                while (i < length && IsSpace(span[i]))
                {
                    i++;
                }

                continue;
            }

            if (c == '\uFEFF')
            {
                i++;
                continue;
            }

            if (c == '\v')
            {
                return false;
            }

            if (c == '-' && next == '-')
            {
                var rel = span[i..].IndexOf('\n');
                i = rel < 0 ? length : i + rel;
                continue;
            }

            if (c == '/' && next == '*')
            {
                var rel = span[(i + 2)..].IndexOf("*/", StringComparison.Ordinal);
                i = rel < 0 ? length : i + 2 + rel + 2;
                continue;
            }

            var start = i;
            SqlTokenKind kind;
            string value;
            switch (c)
            {
                case '\'' or '"' or '`':
                    if (!TryQuoted(sql, span, ref i, c, out value))
                    {
                        return false;
                    }

                    kind = c == '\'' ? SqlTokenKind.String : SqlTokenKind.Quoted;
                    break;
                case '[':
                    var relClose = span[(i + 1)..].IndexOf(']');
                    if (relClose < 0)
                    {
                        return false;
                    }

                    var close = i + 1 + relClose;
                    (kind, value, i) = (SqlTokenKind.Quoted, sql[(i + 1)..close], close + 1);
                    break;
                case 'x' or 'X' when next == '\'':
                    i++;
                    if (!TryQuoted(sql, span, ref i, '\'', out value))
                    {
                        return false;
                    }

                    kind = SqlTokenKind.Blob;
                    break;
                case '?':
                    i++;
                    while (i < length && char.IsAsciiDigit(span[i]))
                    {
                        i++;
                    }

                    (kind, value) = (SqlTokenKind.Parameter, i == start + 1 ? "?" : sql[start..i]);
                    break;
                case ':' or '@' or '$' or '#' when Variable(span, i) is not 0 and var varLength:
                    if (varLength < 0)
                    {
                        return false;
                    }

                    (kind, value, i) = (SqlTokenKind.Parameter, sql[start..(start + varLength)], start + varLength);
                    break;
                case '#':
                    return false;
                case '.' when !char.IsAsciiDigit(next):
                    (kind, value, i) = (SqlTokenKind.Dot, ".", i + 1);
                    break;
                case var digit when char.IsAsciiDigit(digit) || digit == '.':
                    i++;
                    while (i < length && (IsWordPart(span[i]) || span[i] == '.' || (span[i] is '+' or '-' && span[i - 1] is 'e' or 'E')))
                    {
                        i++;
                    }

                    (kind, value) = (SqlTokenKind.Number, sql[start..i]);
                    break;
                case var letter when IsWordStart(letter):
                    i++;
                    while (i < length && IsWordPart(span[i]))
                    {
                        i++;
                    }

                    (kind, value) = (SqlTokenKind.Word, sql[start..i]);
                    break;
                case '-' when next == '>' && i + 2 < length && span[i + 2] == '>':
                    (kind, value, i) = (SqlTokenKind.Symbol, "->>", i + 3);
                    break;
                case '<' when next == '>':
                    (kind, value, i) = (SqlTokenKind.Symbol, "<>", i + 2);
                    break;
                case '<' when next == '=':
                    (kind, value, i) = (SqlTokenKind.Symbol, "<=", i + 2);
                    break;
                case '<' when next == '<':
                    (kind, value, i) = (SqlTokenKind.Symbol, "<<", i + 2);
                    break;
                case '>' when next == '=':
                    (kind, value, i) = (SqlTokenKind.Symbol, ">=", i + 2);
                    break;
                case '>' when next == '>':
                    (kind, value, i) = (SqlTokenKind.Symbol, ">>", i + 2);
                    break;
                case '!' when next == '=':
                    (kind, value, i) = (SqlTokenKind.Symbol, "!=", i + 2);
                    break;
                case '=' when next == '=':
                    (kind, value, i) = (SqlTokenKind.Symbol, "==", i + 2);
                    break;
                case '|' when next == '|':
                    (kind, value, i) = (SqlTokenKind.Symbol, "||", i + 2);
                    break;
                case '-' when next == '>':
                    (kind, value, i) = (SqlTokenKind.Symbol, "->", i + 2);
                    break;
                default:
                    (kind, value) = c switch
                    {
                        ';' => (SqlTokenKind.Semicolon, ";"),
                        '(' => (SqlTokenKind.Open, "("),
                        ')' => (SqlTokenKind.Close, ")"),
                        ',' => (SqlTokenKind.Comma, ","),
                        '=' => (SqlTokenKind.Symbol, "="),
                        '<' => (SqlTokenKind.Symbol, "<"),
                        '>' => (SqlTokenKind.Symbol, ">"),
                        '+' => (SqlTokenKind.Symbol, "+"),
                        '-' => (SqlTokenKind.Symbol, "-"),
                        '*' => (SqlTokenKind.Symbol, "*"),
                        '/' => (SqlTokenKind.Symbol, "/"),
                        '%' => (SqlTokenKind.Symbol, "%"),
                        '|' => (SqlTokenKind.Symbol, "|"),
                        '&' => (SqlTokenKind.Symbol, "&"),
                        '~' => (SqlTokenKind.Symbol, "~"),
                        '!' => (SqlTokenKind.Symbol, "!"),
                        _ => (SqlTokenKind.Symbol, sql[i..(i + 1)]),
                    };
                    i++;
                    break;
            }

            tokens.Add(new SqlToken(kind, start, i - start, value));
        }

        return true;
    }

    private static bool TryQuoted(string sql, ReadOnlySpan<char> span, ref int i, char quote, out string value)
    {
        var contentStart = i + 1;
        var rel = span[contentStart..].IndexOf(quote);
        if (rel < 0)
        {
            value = "";
            return false;
        }

        var firstQuote = contentStart + rel;
        if (firstQuote + 1 >= span.Length || span[firstQuote + 1] != quote)
        {
            i = firstQuote + 1;
            value = firstQuote == contentStart ? "" : sql[contentStart..firstQuote];
            return true;
        }

        var text = new StringBuilder(span.Length - contentStart);
        text.Append(span.Slice(contentStart, rel));
        text.Append(quote);
        for (var j = firstQuote + 2; j < span.Length; j++)
        {
            if (span[j] != quote)
            {
                text.Append(span[j]);
                continue;
            }

            if (j + 1 < span.Length && span[j + 1] == quote)
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

    public static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private static bool StartsSpace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';

    private static int Variable(ReadOnlySpan<char> sql, int start)
    {
        var named = 0;
        var i = start + 1;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (IsWordPart(c))
            {
                named++;
                i++;
            }
            else if (c == '(' && named > 0)
            {
                i++;
                while (i < sql.Length && !IsSpace(sql[i]) && sql[i] is not (')' or '\0'))
                {
                    i++;
                }

                return i < sql.Length && sql[i] == ')' ? i + 1 - start : -1;
            }
            else if (c == ':' && i + 1 < sql.Length && sql[i + 1] == ':')
            {
                i += 2;
            }
            else
            {
                break;
            }
        }

        return named > 0 ? i - start : 0;
    }

    private static bool IsWordStart(char c) => char.IsAsciiLetter(c) || c == '_' || c >= 0x80;

    private static bool IsWordPart(char c) => IsWordStart(c) || char.IsAsciiDigit(c) || c == '$';
}
