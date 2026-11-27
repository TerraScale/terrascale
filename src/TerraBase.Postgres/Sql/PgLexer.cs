using System.Buffers;
using System.Runtime.CompilerServices;
using TerraBase.Postgres.Protocol;

namespace TerraBase.Postgres.Sql;

internal enum PgTokenKind : byte
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
    private const byte ClassWhitespace = 1 << 0;
    private const byte ClassIdentStart = 1 << 1;
    private const byte ClassIdentPart = 1 << 2;
    private const byte ClassDigit = 1 << 3;
    private const byte ClassOperator = 1 << 4;
    private const byte ClassSpecialOperator = 1 << 5;

    private static ReadOnlySpan<byte> AsciiClasses =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 48, 0, 48, 4, 48, 48, 0, 0, 0, 16, 16, 0, 16, 0, 16,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 0, 0, 16, 16, 16, 48,
        48, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 0, 0, 0, 48, 6,
        48, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 0, 48, 0, 48, 0,
    ];

    public static PgTokenizeResult Tokenize(string sql)
    {
        var estimated = (sql.Length >> 2) + 16;
        if (estimated <= 128)
        {
            Span<PgToken> stackBuffer = stackalloc PgToken[estimated];
            PgToken[]? rented = null;
            try
            {
                if (TryTokenizeSpan(sql, stackBuffer, ref rented, out var count) is { } error)
                {
                    return error;
                }

                return (rented is not null ? rented.AsSpan(0, count) : stackBuffer[..count]).ToArray();
            }
            finally
            {
                if (rented is not null)
                {
                    ArrayPool<PgToken>.Shared.Return(rented);
                }
            }
        }

        var buffer = ArrayPool<PgToken>.Shared.Rent(estimated);
        try
        {
            if (TryTokenize(sql, ref buffer, out var count) is { } error)
            {
                return error;
            }

            return buffer.AsSpan(0, count).ToArray();
        }
        finally
        {
            ArrayPool<PgToken>.Shared.Return(buffer);
        }
    }

    internal static PgError? TryTokenize(string sql, ref PgToken[] buffer, out int count)
    {
        PgToken[]? rented = buffer;
        var error = TryTokenizeSpan(sql, buffer, ref rented, out count);
        if (rented is not null)
        {
            buffer = rented;
        }

        return error;
    }

    private static PgError? TryTokenizeSpan(string sql, Span<PgToken> initial, ref PgToken[]? rented, out int count)
    {
        var span = sql.AsSpan();
        var length = span.Length;
        var classes = AsciiClasses;
        var destination = rented is not null ? rented.AsSpan() : initial;
        count = 0;
        var index = 0;
        while (index < length)
        {
            var start = index;
            var current = span[index];
            var next = index + 1 < length ? span[index + 1] : '\0';
            PgTokenKind kind;
            var cls = current < 128 ? classes[current] : (byte)0;
            if ((cls & ClassWhitespace) != 0 || (current >= 128 && char.IsWhiteSpace(current)))
            {
                index++;
                while (index < length)
                {
                    var ch = span[index];
                    if (ch < 128 ? (classes[ch] & ClassWhitespace) != 0 : char.IsWhiteSpace(ch))
                    {
                        index++;
                    }
                    else
                    {
                        break;
                    }
                }

                kind = PgTokenKind.Whitespace;
            }
            else if ((cls & ClassIdentStart) != 0)
            {
                if (next == '\'' && current is 'E' or 'e')
                {
                    index++;
                    if (!SkipQuoted(span, ref index, '\'', backslashEscapes: true))
                    {
                        return PgError.Syntax("unterminated quoted string");
                    }

                    kind = PgTokenKind.EscapeString;
                }
                else if (next == '\'' && current is 'X' or 'x' or 'B' or 'b')
                {
                    index++;
                    if (!SkipQuoted(span, ref index, '\'', backslashEscapes: false))
                    {
                        return PgError.Syntax("unterminated bit string literal");
                    }

                    kind = PgTokenKind.String;
                }
                else
                {
                    index++;
                    while (index < length)
                    {
                        var ch = span[index];
                        if (ch < 128 ? (classes[ch] & ClassIdentPart) != 0 : char.IsLetter(ch))
                        {
                            index++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    kind = PgTokenKind.Word;
                }
            }
            else if (current == '-' && next == '-')
            {
                var end = span[index..].IndexOf('\n');
                index = end < 0 ? length : index + end;
                kind = PgTokenKind.Whitespace;
            }
            else if (current == '/' && next == '*')
            {
                if (!SkipBlockComment(span, ref index))
                {
                    return PgError.Syntax("unterminated /* comment");
                }

                kind = PgTokenKind.Whitespace;
            }
            else if (current == '\'')
            {
                if (!SkipQuoted(span, ref index, '\'', backslashEscapes: false))
                {
                    return PgError.Syntax("unterminated quoted string");
                }

                kind = PgTokenKind.String;
            }
            else if (current == '"')
            {
                if (!SkipQuoted(span, ref index, '"', backslashEscapes: false))
                {
                    return PgError.Syntax("unterminated quoted identifier");
                }

                kind = PgTokenKind.QuotedIdentifier;
            }
            else if (current == '$' && char.IsAsciiDigit(next))
            {
                index += 2;
                while (index < length && char.IsAsciiDigit(span[index]))
                {
                    index++;
                }

                kind = PgTokenKind.Parameter;
            }
            else if (current == '$' && TryDollarTag(span, index, out var tagLength))
            {
                var close = span[(index + tagLength)..].IndexOf(span.Slice(index, tagLength), StringComparison.Ordinal);
                if (close < 0)
                {
                    return PgError.Syntax("unterminated dollar-quoted string");
                }

                index += tagLength + close + tagLength;
                kind = PgTokenKind.DollarString;
            }
            else if ((cls & ClassDigit) != 0 || (current == '.' && char.IsAsciiDigit(next)))
            {
                ScanNumber(span, ref index);
                kind = PgTokenKind.Number;
            }
            else if (current >= 128 && char.IsLetter(current))
            {
                index++;
                while (index < length)
                {
                    var ch = span[index];
                    if (ch < 128 ? (classes[ch] & ClassIdentPart) != 0 : char.IsLetter(ch))
                    {
                        index++;
                    }
                    else
                    {
                        break;
                    }
                }

                kind = PgTokenKind.Word;
            }
            else
            {
                switch (current)
                {
                    case '(':
                        kind = PgTokenKind.LeftParen;
                        index++;
                        break;
                    case ')':
                        kind = PgTokenKind.RightParen;
                        index++;
                        break;
                    case '[':
                        kind = PgTokenKind.LeftBracket;
                        index++;
                        break;
                    case ']':
                        kind = PgTokenKind.RightBracket;
                        index++;
                        break;
                    case ',':
                        kind = PgTokenKind.Comma;
                        index++;
                        break;
                    case ';':
                        kind = PgTokenKind.Semicolon;
                        index++;
                        break;
                    case '.':
                        kind = PgTokenKind.Period;
                        index++;
                        break;
                    case ':' when next == ':':
                        kind = PgTokenKind.DoubleColon;
                        index += 2;
                        break;
                    default:
                        kind = PgTokenKind.Operator;
                        index = (cls & ClassOperator) != 0 ? ScanOperator(span, index, classes) : index + 1;
                        break;
                }
            }

            if (count == destination.Length)
            {
                var grown = ArrayPool<PgToken>.Shared.Rent(destination.Length * 2);
                destination.CopyTo(grown);
                if (rented is not null)
                {
                    ArrayPool<PgToken>.Shared.Return(rented);
                }

                rented = grown;
                destination = grown;
            }

            destination[count++] = new PgToken(kind, start, index - start);
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsIdentifierStart(char character) =>
        character < 128 ? (AsciiClasses[character] & ClassIdentStart) != 0 : char.IsLetter(character);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsIdentifierPart(char character) =>
        character < 128 ? (AsciiClasses[character] & ClassIdentPart) != 0 : char.IsLetter(character);

    private static bool SkipBlockComment(ReadOnlySpan<char> sql, ref int index)
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

    private static bool SkipQuoted(ReadOnlySpan<char> sql, ref int index, char quote, bool backslashEscapes)
    {
        index++;
        if (!backslashEscapes)
        {
            while (index < sql.Length)
            {
                var found = sql[index..].IndexOf(quote);
                if (found < 0)
                {
                    index = sql.Length;
                    return false;
                }

                index += found + 1;
                if (index < sql.Length && sql[index] == quote)
                {
                    index++;
                    continue;
                }

                return true;
            }

            return false;
        }

        while (index < sql.Length)
        {
            var current = sql[index];
            if (current == '\\')
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

    private static bool TryDollarTag(ReadOnlySpan<char> sql, int index, out int length)
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

    private static void ScanNumber(ReadOnlySpan<char> sql, ref int index)
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

    private static int ScanOperator(ReadOnlySpan<char> sql, int index, ReadOnlySpan<byte> classes)
    {
        var start = index;
        var hasSpecial = false;
        while (index < sql.Length)
        {
            var ch = sql[index];
            if (ch >= 128)
            {
                break;
            }

            var cls = classes[ch];
            if ((cls & ClassOperator) == 0)
            {
                break;
            }

            if (index > start && index + 1 < sql.Length && ((ch == '-' && sql[index + 1] == '-') || (ch == '/' && sql[index + 1] == '*')))
            {
                break;
            }

            if ((cls & ClassSpecialOperator) != 0)
            {
                hasSpecial = true;
            }

            index++;
        }

        var length = index - start;
        if (length > 1 && !hasSpecial)
        {
            while (length > 1 && sql[start + length - 1] is '+' or '-')
            {
                length--;
            }
        }

        return start + length;
    }
}

internal readonly union PgTokenizeResult(PgToken[], PgError);
