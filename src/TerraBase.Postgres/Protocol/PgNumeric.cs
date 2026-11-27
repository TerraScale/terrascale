using System.Buffers;
using System.Buffers.Binary;

namespace TerraBase.Postgres.Protocol;

internal static class PgNumeric
{
    private const ushort Positive = 0x0000;
    private const ushort Negative = 0x4000;
    private const ushort NotANumber = 0xC000;
    private const int MaxIntegerDigits = 1000;
    private const int MaxScale = 1000;
    private const int MaxWeight = ((MaxIntegerDigits + 3) / 4) - 1;
    private const int MinWeight = -((MaxScale + 3) / 4);
    private const int MaxGroups = MaxWeight - MinWeight + 1;
    private const int StackGroups = 256;

    public static bool IsDecimalText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        var index = text[0] is '-' or '+' ? 1 : 0;
        var digits = 0;
        var dot = false;
        for (; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsAsciiDigit(character))
            {
                digits++;
            }
            else if (character == '.' && !dot)
            {
                dot = true;
            }
            else
            {
                return false;
            }
        }

        return digits > 0;
    }

    public static bool IsDecimalUtf8(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        var index = text[0] is (byte)'-' or (byte)'+' ? 1 : 0;
        var digits = 0;
        var dot = false;
        for (; index < text.Length; index++)
        {
            var current = text[index];
            if ((uint)(current - '0') <= 9u)
            {
                digits++;
            }
            else if (current == (byte)'.' && !dot)
            {
                dot = true;
            }
            else
            {
                return false;
            }
        }

        return digits > 0;
    }

    public static bool Fits(ReadOnlySpan<char> text)
    {
        var body = !text.IsEmpty && text[0] is '-' or '+' ? text[1..] : text;
        var dot = body.IndexOf('.');
        var integerDigits = (dot < 0 ? body : body[..dot]).TrimStart('0').Length;
        var scale = dot < 0 ? 0 : body.Length - dot - 1;
        return integerDigits <= MaxIntegerDigits && scale <= MaxScale;
    }

    public static bool TryDecode(ReadOnlySpan<byte> value, out string text)
    {
        text = string.Empty;
        if (value.Length < 8)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadInt16BigEndian(value);
        var weight = BinaryPrimitives.ReadInt16BigEndian(value[2..]);
        var sign = BinaryPrimitives.ReadUInt16BigEndian(value[4..]);
        var scale = BinaryPrimitives.ReadInt16BigEndian(value[6..]);
        if (sign == NotANumber)
        {
            return count == 0 && weight == 0 && scale == 0 && value.Length == 8 && TrySet(out text, "NaN");
        }

        if (sign is not (Positive or Negative) || count is < 0 or > MaxGroups || scale is < 0 or > MaxScale
            || weight is < MinWeight or > MaxWeight || weight - count + 1 < MinWeight || value.Length != 8 + (2 * count))
        {
            return false;
        }

        for (var group = 0; group < count; group++)
        {
            if (BinaryPrimitives.ReadInt16BigEndian(value[(8 + (2 * group))..]) is < 0 or > 9999)
            {
                return false;
            }
        }

        var maxIntegerChars = weight >= 0 ? (weight + 1) * 4 : 1;
        var maxLength = (sign == Negative ? 1 : 0) + maxIntegerChars + (scale > 0 ? 1 + scale : 0);
        char[]? rented = null;
        var destination = maxLength <= 256
            ? stackalloc char[maxLength]
            : (rented = ArrayPool<char>.Shared.Rent(maxLength)).AsSpan(0, maxLength);
        try
        {
            var pos = 0;
            if (sign == Negative)
            {
                destination[pos++] = '-';
            }

            if (weight >= 0)
            {
                for (var group = 0; group <= weight; group++)
                {
                    var digit = group < count ? BinaryPrimitives.ReadInt16BigEndian(value[(8 + (2 * group))..]) : (short)0;
                    if (group == 0)
                    {
                        WriteFirstGroup(destination, ref pos, digit);
                    }
                    else
                    {
                        WritePaddedGroup(destination, ref pos, digit);
                    }
                }
            }
            else
            {
                destination[pos++] = '0';
            }

            if (scale > 0)
            {
                destination[pos++] = '.';
                var fracWritten = 0;
                var zeroGroups = -weight - 1;
                for (var group = 0; group < zeroGroups && fracWritten < scale; group++)
                {
                    var zeros = Math.Min(4, scale - fracWritten);
                    destination.Slice(pos, zeros).Fill('0');
                    pos += zeros;
                    fracWritten += zeros;
                }

                Span<char> tail = stackalloc char[4];
                for (var group = Math.Max(weight + 1, 0); group < count && fracWritten < scale; group++)
                {
                    var digit = BinaryPrimitives.ReadInt16BigEndian(value[(8 + (2 * group))..]);
                    var remaining = scale - fracWritten;
                    if (remaining >= 4)
                    {
                        WritePaddedGroup(destination, ref pos, digit);
                        fracWritten += 4;
                    }
                    else
                    {
                        var tailPos = 0;
                        WritePaddedGroup(tail, ref tailPos, digit);
                        tail[..remaining].CopyTo(destination[pos..]);
                        pos += remaining;
                        fracWritten += remaining;
                    }
                }

                if (fracWritten < scale)
                {
                    var pad = scale - fracWritten;
                    destination.Slice(pos, pad).Fill('0');
                    pos += pad;
                }
            }

            text = new string(destination[..pos]);
            return true;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    public static bool TryEncode(ReadOnlySpan<char> text, PgMessageBuffer output)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        var negative = text[0] == '-';
        var body = text[0] is '-' or '+' ? text[1..] : text;
        var dot = -1;
        var digits = 0;
        for (var index = 0; index < body.Length; index++)
        {
            var character = body[index];
            if ((uint)(character - '0') <= 9u)
            {
                digits++;
            }
            else if (character == '.' && dot < 0)
            {
                dot = index;
            }
            else
            {
                return false;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        var integerPart = (dot < 0 ? body : body[..dot]).TrimStart('0');
        var fractionPart = dot < 0 ? [] : body[(dot + 1)..];
        if (integerPart.Length > MaxIntegerDigits || fractionPart.Length > MaxScale)
        {
            return false;
        }

        var length = ((integerPart.Length + 3) / 4) + ((fractionPart.Length + 3) / 4);
        short[]? rented = null;
        Span<short> groups = length <= StackGroups ? stackalloc short[length] : rented = ArrayPool<short>.Shared.Rent(length);
        try
        {
            Encode(negative, integerPart, fractionPart, groups[..length], output);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<short>.Shared.Return(rented);
            }
        }

        return true;
    }

    private static void Encode(bool negative, ReadOnlySpan<char> integerPart, ReadOnlySpan<char> fractionPart, Span<short> groups, PgMessageBuffer output)
    {
        var integerGroups = (integerPart.Length + 3) / 4;
        var fractionGroups = (fractionPart.Length + 3) / 4;
        for (var group = 0; group < integerGroups; group++)
        {
            var end = integerPart.Length - ((integerGroups - 1 - group) * 4);
            var start = Math.Max(0, end - 4);
            var value = 0;
            for (var index = start; index < end; index++)
            {
                value = (value * 10) + (integerPart[index] - '0');
            }

            groups[group] = (short)value;
        }

        for (var group = 0; group < fractionGroups; group++)
        {
            var start = group * 4;
            var end = Math.Min(fractionPart.Length, start + 4);
            var value = 0;
            for (var index = start; index < end; index++)
            {
                value = (value * 10) + (fractionPart[index] - '0');
            }

            for (var pad = end - start; pad < 4; pad++)
            {
                value *= 10;
            }

            groups[integerGroups + group] = (short)value;
        }

        var first = 0;
        while (first < groups.Length && groups[first] == 0)
        {
            first++;
        }

        var last = groups.Length - 1;
        while (last >= first && groups[last] == 0)
        {
            last--;
        }

        var count = Math.Max(0, last - first + 1);
        var weight = count == 0 ? 0 : integerGroups - 1 - first;
        var byteCount = 8 + (count * 2);
        var span = output.GetSpan(byteCount);
        BinaryPrimitives.WriteInt16BigEndian(span, (short)count);
        BinaryPrimitives.WriteInt16BigEndian(span[2..], (short)weight);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], negative && count > 0 ? Negative : Positive);
        BinaryPrimitives.WriteInt16BigEndian(span[6..], (short)fractionPart.Length);
        var offset = 8;
        for (var group = first; group <= last; group++)
        {
            BinaryPrimitives.WriteInt16BigEndian(span[offset..], groups[group]);
            offset += 2;
        }

        output.Advance(byteCount);
    }

    private static void WriteFirstGroup(Span<char> destination, ref int pos, short digit)
    {
        if (digit >= 1000)
        {
            destination[pos++] = (char)('0' + (digit / 1000));
            destination[pos++] = (char)('0' + ((digit / 100) % 10));
            destination[pos++] = (char)('0' + ((digit / 10) % 10));
            destination[pos++] = (char)('0' + (digit % 10));
        }
        else if (digit >= 100)
        {
            destination[pos++] = (char)('0' + (digit / 100));
            destination[pos++] = (char)('0' + ((digit / 10) % 10));
            destination[pos++] = (char)('0' + (digit % 10));
        }
        else if (digit >= 10)
        {
            destination[pos++] = (char)('0' + (digit / 10));
            destination[pos++] = (char)('0' + (digit % 10));
        }
        else
        {
            destination[pos++] = (char)('0' + digit);
        }
    }

    private static void WritePaddedGroup(Span<char> destination, ref int pos, short digit)
    {
        destination[pos++] = (char)('0' + (digit / 1000));
        destination[pos++] = (char)('0' + ((digit / 100) % 10));
        destination[pos++] = (char)('0' + ((digit / 10) % 10));
        destination[pos++] = (char)('0' + (digit % 10));
    }

    private static bool TrySet(out string target, string value)
    {
        target = value;
        return true;
    }
}
