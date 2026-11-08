using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerraBase.Features.Postgres;

internal static class PgNumeric
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly Regex Pattern = new(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled);

    public static bool Matches(string text) => Pattern.IsMatch(text);

    public static string? Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 8)
        {
            return null;
        }

        var ndigits = BinaryPrimitives.ReadInt16BigEndian(b);
        var weight = BinaryPrimitives.ReadInt16BigEndian(b[2..]);
        var sign = BinaryPrimitives.ReadUInt16BigEndian(b[4..]);
        var dscale = BinaryPrimitives.ReadInt16BigEndian(b[6..]);
        if (sign is 0xC000 or 0xD000 or 0xF000 || b.Length != 8 + 2 * ndigits)
        {
            return null;
        }

        var integer = new StringBuilder();
        if (weight >= 0)
        {
            for (var i = 0; i <= weight; i++)
            {
                var group = i < ndigits ? BinaryPrimitives.ReadInt16BigEndian(b[(8 + 2 * i)..]) : 0;
                integer.Append(i == 0 ? group.ToString(Invariant) : group.ToString("D4", Invariant));
            }
        }
        else
        {
            integer.Append('0');
        }

        var fraction = new StringBuilder();
        for (var i = 0; i < -weight - 1; i++)
        {
            fraction.Append("0000");
        }

        for (var i = weight + 1 > 0 ? weight + 1 : 0; i < ndigits; i++)
        {
            fraction.Append(BinaryPrimitives.ReadInt16BigEndian(b[(8 + 2 * i)..]).ToString("D4", Invariant));
        }

        if (fraction.Length > dscale)
        {
            fraction.Remove(dscale, fraction.Length - dscale);
        }
        else
        {
            fraction.Append('0', dscale - fraction.Length);
        }

        return (sign == 0x4000 ? "-" : "") + integer + (dscale > 0 ? "." + fraction : "");
    }

    public static byte[]? Encode(string text)
    {
        if (!Pattern.IsMatch(text))
        {
            return null;
        }

        var negative = text.StartsWith('-');
        var body = negative ? text[1..] : text;
        var dot = body.IndexOf('.');
        var integerPart = dot < 0 ? body : body[..dot];
        var fractionPart = dot < 0 ? "" : body[(dot + 1)..];
        integerPart = integerPart.TrimStart('0');
        if (integerPart.Length == 0)
        {
            integerPart = "0";
        }

        var dscale = fractionPart.Length;
        var paddedInteger = integerPart.PadLeft((integerPart.Length + 3) / 4 * 4, '0');
        var paddedFraction = fractionPart.PadRight((fractionPart.Length + 3) / 4 * 4, '0');
        List<int> groups = [];
        for (var i = 0; i < paddedInteger.Length; i += 4)
        {
            groups.Add(int.Parse(paddedInteger[i..(i + 4)], Invariant));
        }

        for (var i = 0; i < paddedFraction.Length; i += 4)
        {
            groups.Add(int.Parse(paddedFraction[i..(i + 4)], Invariant));
        }

        var weight = paddedInteger.Length / 4 - 1;
        while (groups.Count > 0 && groups[0] == 0)
        {
            groups.RemoveAt(0);
            weight--;
        }

        while (groups.Count > 0 && groups[^1] == 0)
        {
            groups.RemoveAt(groups.Count - 1);
        }

        var ndigits = groups.Count;
        if (ndigits == 0)
        {
            weight = 0;
        }

        var bytes = new byte[8 + 2 * ndigits];
        BinaryPrimitives.WriteInt16BigEndian(bytes, (short)ndigits);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(2), (short)weight);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), (ushort)(negative && ndigits > 0 ? 0x4000 : 0));
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(6), (short)dscale);
        for (var i = 0; i < ndigits; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(8 + 2 * i), (short)groups[i]);
        }

        return bytes;
    }
}
