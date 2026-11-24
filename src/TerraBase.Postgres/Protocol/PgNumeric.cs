using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

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
            return count == 0 && value.Length == 8 && TrySet(out text, "NaN");
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

        var integer = new StringBuilder();
        if (weight >= 0)
        {
            for (var group = 0; group <= weight; group++)
            {
                var digit = group < count ? BinaryPrimitives.ReadInt16BigEndian(value[(8 + (2 * group))..]) : (short)0;
                integer.Append(digit.ToString(group == 0 ? "D" : "D4", CultureInfo.InvariantCulture));
            }
        }
        else
        {
            integer.Append('0');
        }

        var fraction = new StringBuilder();
        for (var group = 0; group < -weight - 1; group++)
        {
            fraction.Append("0000");
        }

        for (var group = Math.Max(weight + 1, 0); group < count; group++)
        {
            fraction.Append(BinaryPrimitives.ReadInt16BigEndian(value[(8 + (2 * group))..]).ToString("D4", CultureInfo.InvariantCulture));
        }

        if (fraction.Length > scale)
        {
            fraction.Length = scale;
        }
        else
        {
            fraction.Append('0', scale - fraction.Length);
        }

        text = (sign == Negative ? "-" : string.Empty) + integer + (scale > 0 ? "." + fraction : string.Empty);
        return true;
    }

    public static bool TryEncode(ReadOnlySpan<char> text, PgMessageBuffer output)
    {
        if (!IsDecimalText(text) || !Fits(text))
        {
            return false;
        }

        var body = text[0] is '-' or '+' ? text[1..] : text;
        var dot = body.IndexOf('.');
        var integerPart = (dot < 0 ? body : body[..dot]).TrimStart('0');
        var fractionPart = dot < 0 ? [] : body[(dot + 1)..];
        var length = ((integerPart.Length + 3) / 4) + ((fractionPart.Length + 3) / 4);
        short[]? rented = null;
        Span<short> groups = length <= StackGroups ? stackalloc short[StackGroups] : rented = ArrayPool<short>.Shared.Rent(length);
        try
        {
            Encode(text[0] == '-', integerPart, fractionPart, groups[..length], output);
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
            groups[group] = short.Parse(integerPart[start..end], NumberStyles.None, CultureInfo.InvariantCulture);
        }

        for (var group = 0; group < fractionGroups; group++)
        {
            Span<char> digits = ['0', '0', '0', '0'];
            var slice = fractionPart[(group * 4)..Math.Min(fractionPart.Length, (group * 4) + 4)];
            slice.CopyTo(digits);
            groups[integerGroups + group] = short.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
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
        output.WriteInt16((short)count);
        output.WriteInt16((short)weight);
        output.WriteInt16(unchecked((short)(negative && count > 0 ? Negative : Positive)));
        output.WriteInt16((short)fractionPart.Length);
        for (var group = first; group <= last; group++)
        {
            output.WriteInt16(groups[group]);
        }
    }

    private static bool TrySet(out string target, string value)
    {
        target = value;
        return true;
    }
}
