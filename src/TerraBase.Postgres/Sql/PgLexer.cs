using System.Buffers;
using TerraBase.Postgres.Protocol;

namespace TerraBase.Postgres.Sql;

internal enum PgTokenKind
{
    Whitespace,
    Word,
    QuotedIdentifier,
    String,
    EscapeString,
    DollarString,
    Number,
    Parameter,
    Operator,
    LeftParen,
    RightParen,
    LeftBracket,
    RightBracket,
    Comma,
    Semicolon,
    Period,
    DoubleColon,
}

internal readonly record struct PgToken(PgTokenKind Kind, int Start, int Length);

internal static class PgLexer
{
    private const string OperatorCharacters = "+-*/<>=~!@#%^&|`?";
    private static readonly SearchValues<char> SpecialOperatorCharacters = SearchValues.Create("~!@#%^&|`?");

    public static Result<PgToken[], PgError> Tokenize(string sql)
    {
        var tokens = new List<PgToken>((sql.Length / 4) + 4);
        var index = 0;
        while (index < sql.Length)
        {
            var start = index;
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            PgTokenKind kind;
            if (char.IsWhiteSpace(current))
            {
                while (index < sql.Length && char.IsWhiteSpace(sql[index]))
                {
                    index++;
                }

                kind = PgTokenKind.Whitespace;
            }
            else if (current == '-' && next == '-')
            {
                var end = sql.IndexOf('\n', index);
                index = end < 0 ? sql.Length : end;
                kind = PgTokenKind.Whitespace;
            }
            else if (current == '/' && next == '*')
            {
                if (!SkipBlockComment(sql, ref index))
                {
                    return PgError.Syntax("unterminated /* comment");
                }

                kind = PgTokenKind.Whitespace;
            }
            else if (current == '\'')
            {
                if (!SkipQuoted(sql, ref index, '\'', backslashEscapes: false))
                {
                    return PgError.Syntax("unterminated quoted string");
                }

                kind = PgTokenKind.String;
            }
            else if (current is 'E' or 'e' && next == '\'')
            {
                index++;
                if (!SkipQuoted(sql, ref index, '\'', backslashEscapes: true))
                {
                    return PgError.Syntax("unterminated quoted string");
                }

                kind = PgTokenKind.EscapeString;
            }
            else if (current is 'X' or 'x' or 'B' or 'b' && next == '\'')
            {
                index++;
                if (!SkipQuoted(sql, ref index, '\'', backslashEscapes: false))
                {
                    return PgError.Syntax("unterminated bit string literal");
                }

                kind = PgTokenKind.String;
            }
            else if (current == '"')
            {
                if (!SkipQuoted(sql, ref index, '"', backslashEscapes: false))
                {
                    return PgError.Syntax("unterminated quoted identifier");
                }

                kind = PgTokenKind.QuotedIdentifier;
            }
            else if (current == '$' && char.IsAsciiDigit(next))
            {
                index++;
                while (index < sql.Length && char.IsAsciiDigit(sql[index]))
                {
                    index++;
                }

                kind = PgTokenKind.Parameter;
            }
            else if (current == '$' && TryDollarTag(sql, index, out var tagLength))
            {
                var close = sql.AsSpan(index + tagLength).IndexOf(sql.AsSpan(index, tagLength), StringComparison.Ordinal);
                if (close < 0)
                {
                    return PgError.Syntax("unterminated dollar-quoted string");
                }

                index += tagLength + close + tagLength;
                kind = PgTokenKind.DollarString;
            }
            else if (IsIdentifierStart(current))
            {
                index++;
                while (index < sql.Length && IsIdentifierPart(sql[index]))
                {
                    index++;
                }

                kind = PgTokenKind.Word;
            }
            else if (char.IsAsciiDigit(current) || (current == '.' && char.IsAsciiDigit(next)))
            {
                ScanNumber(sql, ref index);
                kind = PgTokenKind.Number;
            }
            else
            {
                (kind, index) = current switch
                {
                    '(' => (PgTokenKind.LeftParen, index + 1),
                    ')' => (PgTokenKind.RightParen, index + 1),
                    '[' => (PgTokenKind.LeftBracket, index + 1),
                    ']' => (PgTokenKind.RightBracket, index + 1),
                    ',' => (PgTokenKind.Comma, index + 1),
                    ';' => (PgTokenKind.Semicolon, index + 1),
                    '.' => (PgTokenKind.Period, index + 1),
                    ':' when next == ':' => (PgTokenKind.DoubleColon, index + 2),
                    _ when OperatorCharacters.Contains(current, StringComparison.Ordinal) => (PgTokenKind.Operator, ScanOperator(sql, index)),
                    _ => (PgTokenKind.Operator, index + 1),
                };
            }

            tokens.Add(new PgToken(kind, start, index - start));
        }

        return tokens.ToArray();
    }

    public static bool IsIdentifierStart(char character) => char.IsAsciiLetter(character) || character == '_' || (character > 127 && char.IsLetter(character));

    public static bool IsIdentifierPart(char character) => IsIdentifierStart(character) || char.IsAsciiDigit(character) || character == '$';

    private static bool SkipBlockComment(string sql, ref int index)
    {
        var depth = 0;
        while (index < sql.Length)
        {
            if (sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                depth++;
                index += 2;
            }
            else if (sql[index] == '*' && index + 1 < sql.Length && sql[index + 1] == '/')
            {
                depth--;
                index += 2;
                if (depth == 0)
                {
                    return true;
                }
            }
            else
            {
                index++;
            }
        }

        return false;
    }

    private static bool SkipQuoted(string sql, ref int index, char quote, bool backslashEscapes)
    {
        index++;
        while (index < sql.Length)
        {
            var current = sql[index];
            if (backslashEscapes && current == '\\')
            {
                index += 2;
                continue;
            }

            if (current == quote)
            {
                if (index + 1 < sql.Length && sql[index + 1] == quote)
                {
                    index += 2;
                    continue;
                }

                index++;
                return true;
            }

            index++;
        }

        return false;
    }

    private static bool TryDollarTag(string sql, int index, out int length)
    {
        length = 0;
        var cursor = index + 1;
        if (cursor < sql.Length && sql[cursor] != '$')
        {
            if (!IsIdentifierStart(sql[cursor]))
            {
                return false;
            }

            while (cursor < sql.Length && (IsIdentifierStart(sql[cursor]) || char.IsAsciiDigit(sql[cursor])))
            {
                cursor++;
            }
        }

        if (cursor >= sql.Length || sql[cursor] != '$')
        {
            return false;
        }

        length = cursor - index + 1;
        return true;
    }

    private static void ScanNumber(string sql, ref int index)
    {
        while (index < sql.Length && (char.IsAsciiDigit(sql[index]) || sql[index] == '_'))
        {
            index++;
        }

        if (index < sql.Length && sql[index] == '.' && !(index + 1 < sql.Length && sql[index + 1] == '.'))
        {
            index++;
            while (index < sql.Length && char.IsAsciiDigit(sql[index]))
            {
                index++;
            }
        }

        if (index < sql.Length && sql[index] is 'e' or 'E')
        {
            var exponent = index + 1;
            if (exponent < sql.Length && sql[exponent] is '+' or '-')
            {
                exponent++;
            }

            if (exponent < sql.Length && char.IsAsciiDigit(sql[exponent]))
            {
                index = exponent;
                while (index < sql.Length && char.IsAsciiDigit(sql[index]))
                {
                    index++;
                }
            }
        }
    }

    private static int ScanOperator(string sql, int index)
    {
        var start = index;
        while (index < sql.Length && OperatorCharacters.Contains(sql[index], StringComparison.Ordinal))
        {
            if (index > start && index + 1 < sql.Length && ((sql[index] == '-' && sql[index + 1] == '-') || (sql[index] == '/' && sql[index + 1] == '*')))
            {
                break;
            }

            index++;
        }

        var length = index - start;
        if (length > 1 && sql.AsSpan(start, length).IndexOfAny(SpecialOperatorCharacters) < 0)
        {
            while (length > 1 && sql[start + length - 1] is '+' or '-')
            {
                length--;
            }
        }

        return start + length;
    }
}
