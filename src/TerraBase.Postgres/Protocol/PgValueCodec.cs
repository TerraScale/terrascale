using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using TerraBase.Postgres.Hosting;

namespace TerraBase.Postgres.Protocol;

internal static class PgValueCodec
{
    private const int TextFormatCode = 0;
    private const int BinaryFormatCode = 1;
    private const int ObjectOverhead = 24;
    private static ReadOnlySpan<byte> HexDigits => "0123456789abcdef"u8;

    private static ReadOnlySpan<sbyte> HexNibbles =>
    [
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, -1, -1, -1, -1, -1, -1,
        -1, 10, 11, 12, 13, 14, 15, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, 10, 11, 12, 13, 14, 15, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
    ];

    public static bool SupportsBinary(int oid) => PgOid.IsKnown(oid) && oid != PgOid.Unknown;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short ResultFormat(IReadOnlyList<short> formats, int index, int oid)
    {
        if (formats.Count == 0)
        {
            return TextFormatCode;
        }

        var requested = formats.Count == 1 ? formats[0] : formats[Math.Min(index, formats.Count - 1)];
        return requested == BinaryFormatCode && SupportsBinary(oid) ? (short)BinaryFormatCode : (short)TextFormatCode;
    }

    public static bool TryWrite(PgMessageBuffer output, SqlValue value, int oid, bool binary)
    {
        if (value.Kind == SqlValueKind.Null)
        {
            output.WriteInt32(-1);
            return true;
        }

        if (binary)
        {
            if (value.Kind == SqlValueKind.Integral && value.TryGetIntegral(out var integral))
            {
                switch (oid)
                {
                    case PgOid.Bool:
                        var boolSpan = output.GetSpan(5);
                        BinaryPrimitives.WriteInt32BigEndian(boolSpan, 1);
                        boolSpan[4] = integral != 0 ? (byte)1 : (byte)0;
                        output.Advance(5);
                        return true;
                    case PgOid.Int2:
                        if (integral is < short.MinValue or > short.MaxValue)
                        {
                            return false;
                        }

                        var int2Span = output.GetSpan(6);
                        BinaryPrimitives.WriteInt32BigEndian(int2Span, 2);
                        BinaryPrimitives.WriteInt16BigEndian(int2Span[4..], (short)integral);
                        output.Advance(6);
                        return true;
                    case PgOid.Int4:
                        if (integral is < int.MinValue or > int.MaxValue)
                        {
                            return false;
                        }

                        var int4Span = output.GetSpan(8);
                        BinaryPrimitives.WriteInt32BigEndian(int4Span, 4);
                        BinaryPrimitives.WriteInt32BigEndian(int4Span[4..], (int)integral);
                        output.Advance(8);
                        return true;
                    case PgOid.Int8:
                        var int8Span = output.GetSpan(12);
                        BinaryPrimitives.WriteInt32BigEndian(int8Span, 8);
                        BinaryPrimitives.WriteInt64BigEndian(int8Span[4..], integral);
                        output.Advance(12);
                        return true;
                }
            }
            else if (oid == PgOid.Float8 && value.Kind == SqlValueKind.Real && value.TryGetReal(out var real))
            {
                var float8Span = output.GetSpan(12);
                BinaryPrimitives.WriteInt32BigEndian(float8Span, 8);
                BinaryPrimitives.WriteDoubleBigEndian(float8Span[4..], real);
                output.Advance(12);
                return true;
            }
            else if (oid == PgOid.Bytea && value.Kind == SqlValueKind.Blob && value.TryGetBlob(out var blob))
            {
                var blobSpan = output.GetSpan(4 + blob.Length);
                BinaryPrimitives.WriteInt32BigEndian(blobSpan, blob.Length);
                blob.Span.CopyTo(blobSpan[4..]);
                output.Advance(4 + blob.Length);
                return true;
            }
        }

        var lengthAt = output.ReserveInt32();
        var start = output.Length;
        var written = binary ? TryWriteBinary(output, value, oid) : TryWriteText(output, value, oid);
        if (!written)
        {
            output.Truncate(lengthAt);
            return false;
        }

        output.PatchInt32(lengthAt, output.Length - start);
        return true;
    }

    public static string? FormatText(SqlValue value, int oid)
    {
        if (value.Kind == SqlValueKind.Null)
        {
            return null;
        }

        using var buffer = new PgMessageBuffer();
        return TryWriteText(buffer, value, oid) ? Encoding.UTF8.GetString(buffer.WrittenSpan) : null;
    }

    public static bool TryWriteText(PgMessageBuffer output, SqlValue value, int oid)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                return false;
            case SqlValueKind.Integral when value.TryGetIntegral(out var integral):
                if (oid == PgOid.Bool)
                {
                    output.WriteByte(integral != 0 ? (byte)'t' : (byte)'f');
                    return true;
                }

                Utf8Formatter.TryFormat(integral, output.GetSpan(20), out var integralLength);
                output.Advance(integralLength);
                return true;
            case SqlValueKind.Real when value.TryGetReal(out var real):
                WriteRealUtf8(output, real, oid);
                return true;
            case SqlValueKind.Text when value.TryGetText(out var text):
                return TryWriteTextValue(output, text, oid);
            case SqlValueKind.Blob when value.TryGetBlob(out var blob):
                output.WriteByte((byte)'\\');
                output.WriteByte((byte)'x');
                WriteHex(output, blob.Span);
                return true;
            default:
                return false;
        }
    }

    public static bool TryDecode(int oid, short format, byte[]? raw, out SqlValue value)
    {
        value = SqlValue.Null;
        if (raw is null)
        {
            return true;
        }

        return format == BinaryFormatCode
            ? TryDecodeBinary(oid, raw, raw, out value)
            : TryDecodeTextUtf8(oid, raw, out value);
    }

    public static bool TryDecode(int oid, short format, ReadOnlySpan<byte> raw, out SqlValue value) =>
        format == BinaryFormatCode
            ? TryDecodeBinary(oid, raw, ownedRaw: null, out value)
            : TryDecodeTextUtf8(oid, raw, out value);

    public static bool TryDecodeText(int oid, string text, out SqlValue value)
    {
        value = SqlValue.Null;
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        if (PgOid.TryGetArrayElement(oid, out var element))
        {
            return TryDecodeTextArray(element, text, out value);
        }

        switch (oid)
        {
            case PgOid.Bool:
                if (!TryParseBool(text, out var flag))
                {
                    return false;
                }

                value = SqlValue.FromIntegral(flag ? 1 : 0);
                return true;
            case PgOid.Int2 or PgOid.Int4 or PgOid.Int8 or PgOid.Oid:
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var integral))
                {
                    return false;
                }

                value = SqlValue.FromIntegral(integral);
                return true;
            case PgOid.Float4 or PgOid.Float8:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) || !double.IsFinite(real))
                {
                    return false;
                }

                value = SqlValue.FromReal(real);
                return true;
            case PgOid.Numeric:
                if (!PgNumeric.IsDecimalText(text))
                {
                    return false;
                }

                value = NumericValue(text);
                return true;
            case PgOid.Bytea:
                return TryDecodeHexBytea(text, out value);
            case PgOid.Uuid:
                if (!Guid.TryParse(text, out var uuid))
                {
                    return false;
                }

                value = SqlValue.FromText(uuid.ToString("D", CultureInfo.InvariantCulture));
                return true;
            case PgOid.Date or PgOid.Time or PgOid.Timestamp or PgOid.Timestamptz:
                if (!PgTemporal.TryParse(oid, text, out var instant))
                {
                    return false;
                }

                value = SqlValue.FromText(PgTemporal.Format(oid, instant));
                return true;
            case 0 or PgOid.Unknown:
                value = UntypedValue(text);
                return true;
            default:
                value = SqlValue.FromText(text);
                return true;
        }
    }

    public static string EncodeFailureState(SqlValue value, int oid) =>
        oid is PgOid.Int2 or PgOid.Int4 || (oid == PgOid.Numeric && NumericText(value) is { } numeric && !PgNumeric.Fits(numeric)) ? "22003" : "22P02";

    public static string DecodeFailureState(int oid, short format) => format == BinaryFormatCode && oid == PgOid.Numeric ? "22P03" : "22P02";

    public static long Footprint(SqlValue value) =>
        Unsafe.SizeOf<SqlValue>()
        + (value.TryGetText(out var text) ? ObjectOverhead + ((long)text.Length * sizeof(char))
            : value.TryGetBlob(out var blob) ? ObjectOverhead + blob.Length
            : 0);

    public static long Footprint(IReadOnlyList<short> formats) => ObjectOverhead + ObjectOverhead + ((long)formats.Count * sizeof(short));

    public static long Footprint(IReadOnlyList<IReadOnlyList<SqlValue>> rows)
    {
        var total = ObjectOverhead + ((long)Slots(rows) * IntPtr.Size);
        foreach (var row in rows)
        {
            total += ObjectOverhead + ((long)(Slots(row) - row.Count) * Unsafe.SizeOf<SqlValue>());
            foreach (var value in row)
            {
                total += Footprint(value);
            }
        }

        return total;
    }

    public static long Footprint(IReadOnlyList<PgColumn>? columns)
    {
        if (columns is null)
        {
            return 0;
        }

        var total = ObjectOverhead + ((long)Slots(columns) * IntPtr.Size);
        foreach (var column in columns)
        {
            total += ObjectOverhead + IntPtr.Size + sizeof(int) + ObjectOverhead + ((long)column.Name.Length * sizeof(char));
        }

        return total;
    }

    private static int Slots<T>(IReadOnlyList<T> list) => list is List<T> owned ? owned.Capacity : list.Count;

    public static bool TryParseBool(ReadOnlySpan<char> text, out bool value)
    {
        var trimmed = text.Trim();
        switch (trimmed.Length)
        {
            case 1:
                switch (trimmed[0])
                {
                    case 't' or 'T' or 'y' or 'Y' or '1':
                        value = true;
                        return true;
                    case 'f' or 'F' or 'n' or 'N' or '0':
                        value = false;
                        return true;
                }

                break;
            case 2:
                if (trimmed.Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    value = true;
                    return true;
                }

                if (trimmed.Equals("no", StringComparison.OrdinalIgnoreCase))
                {
                    value = false;
                    return true;
                }

                break;
            case 3:
                if (trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    value = true;
                    return true;
                }

                if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    value = false;
                    return true;
                }

                break;
            case 4:
                if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    value = true;
                    return true;
                }

                break;
            case 5:
                if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    value = false;
                    return true;
                }

                break;
        }

        value = false;
        return false;
    }

    private static bool TryDecodeTextUtf8(int oid, ReadOnlySpan<byte> raw, out SqlValue value)
    {
        value = SqlValue.Null;
        switch (oid)
        {
            case PgOid.Bool:
                if (!TryParseBoolUtf8(raw, out var flag))
                {
                    return false;
                }

                value = SqlValue.FromIntegral(flag ? 1 : 0);
                return true;
            case PgOid.Int2 or PgOid.Int4 or PgOid.Int8 or PgOid.Oid:
                if (!TryParseInt64Utf8(raw, out var integral))
                {
                    return false;
                }

                value = SqlValue.FromIntegral(integral);
                return true;
            case PgOid.Float4 or PgOid.Float8:
                var floatSpan = TrimAsciiWhitespace(raw);
                if (!Utf8Parser.TryParse(floatSpan, out double real, out var floatConsumed) || floatConsumed != floatSpan.Length || !double.IsFinite(real))
                {
                    return false;
                }

                value = SqlValue.FromReal(real);
                return true;
            case PgOid.Numeric:
                if (!PgNumeric.IsDecimalUtf8(raw))
                {
                    return false;
                }

                if (raw.IndexOf((byte)'.') < 0 && TryParseInt64Utf8(raw, out var whole))
                {
                    value = SqlValue.FromIntegral(whole);
                    return true;
                }

                value = SqlValue.FromText(Encoding.ASCII.GetString(raw));
                return true;
            case PgOid.Bytea:
                return TryDecodeHexByteaUtf8(raw, out value);
            case PgOid.Uuid:
                return TryDecodeUuidUtf8(raw, out value);
            case PgOid.Timestamptz when PgTemporal.IsCanonicalTimestamptzUtf8(raw):
                value = SqlValue.FromText(Encoding.ASCII.GetString(raw));
                return true;
            case PgOid.Date or PgOid.Time or PgOid.Timestamp or PgOid.Timestamptz:
                if (!PgTemporal.TryParseUtf8(oid, raw, out var instant))
                {
                    return false;
                }

                value = SqlValue.FromText(PgTemporal.Format(oid, instant));
                return true;
            case 0 or PgOid.Unknown:
                if (raw.IndexOf((byte)0) >= 0 || !TryDecodeUtf8String(raw, out var untyped))
                {
                    return false;
                }

                value = UntypedValue(untyped);
                return true;
            case PgOid.Text or PgOid.Varchar or PgOid.Bpchar or PgOid.Json or PgOid.Jsonb:
                if (raw.IndexOf((byte)0) >= 0 || !TryDecodeUtf8String(raw, out var decodedText))
                {
                    return false;
                }

                value = SqlValue.FromText(decodedText);
                return true;
            default:
                if (raw.IndexOf((byte)0) >= 0 || !TryDecodeUtf8String(raw, out var fallbackText))
                {
                    return false;
                }

                if (PgOid.TryGetArrayElement(oid, out var element))
                {
                    return TryDecodeTextArray(element, fallbackText, out value);
                }

                value = SqlValue.FromText(fallbackText);
                return true;
        }
    }

    private static bool TryParseInt64Utf8(ReadOnlySpan<byte> raw, out long value)
    {
        var span = TrimAsciiWhitespace(raw);
        var length = span.Length;
        if (length == 0)
        {
            value = 0;
            return false;
        }

        var index = 0;
        var negative = false;
        var first = span[0];
        if (first is (byte)'-' or (byte)'+')
        {
            negative = first == (byte)'-';
            index = 1;
            if (length == 1)
            {
                value = 0;
                return false;
            }
        }

        if (length - index <= 18)
        {
            long acc = 0;
            for (; index < length; index++)
            {
                var digit = span[index] - '0';
                if ((uint)digit > 9u)
                {
                    value = 0;
                    return false;
                }

                acc = (acc * 10) + digit;
            }

            value = negative ? -acc : acc;
            return true;
        }

        return Utf8Parser.TryParse(span, out value, out var consumed) && consumed == length;
    }

    private static bool TryDecodeUtf8String(ReadOnlySpan<byte> raw, out string text)
    {
        if (raw.IsEmpty)
        {
            text = string.Empty;
            return true;
        }

        if (Ascii.IsValid(raw))
        {
            text = Encoding.ASCII.GetString(raw);
            return true;
        }

        if (!Utf8.IsValid(raw))
        {
            text = string.Empty;
            return false;
        }

        text = Encoding.UTF8.GetString(raw);
        return true;
    }

    private static bool TryParseBoolUtf8(ReadOnlySpan<byte> raw, out bool value)
    {
        var trimmed = TrimAsciiWhitespace(raw);
        switch (trimmed.Length)
        {
            case 1:
                switch (trimmed[0])
                {
                    case (byte)'t' or (byte)'T' or (byte)'y' or (byte)'Y' or (byte)'1':
                        value = true;
                        return true;
                    case (byte)'f' or (byte)'F' or (byte)'n' or (byte)'N' or (byte)'0':
                        value = false;
                        return true;
                }

                break;
            case 2:
                if ((trimmed[0] | 0x20) == 'o' && (trimmed[1] | 0x20) == 'n')
                {
                    value = true;
                    return true;
                }

                if ((trimmed[0] | 0x20) == 'n' && (trimmed[1] | 0x20) == 'o')
                {
                    value = false;
                    return true;
                }

                break;
            case 3:
                if ((trimmed[0] | 0x20) == 'y' && (trimmed[1] | 0x20) == 'e' && (trimmed[2] | 0x20) == 's')
                {
                    value = true;
                    return true;
                }

                if ((trimmed[0] | 0x20) == 'o' && (trimmed[1] | 0x20) == 'f' && (trimmed[2] | 0x20) == 'f')
                {
                    value = false;
                    return true;
                }

                break;
            case 4:
                if ((trimmed[0] | 0x20) == 't' && (trimmed[1] | 0x20) == 'r' && (trimmed[2] | 0x20) == 'u' && (trimmed[3] | 0x20) == 'e')
                {
                    value = true;
                    return true;
                }

                break;
            case 5:
                if ((trimmed[0] | 0x20) == 'f' && (trimmed[1] | 0x20) == 'a' && (trimmed[2] | 0x20) == 'l' && (trimmed[3] | 0x20) == 's' && (trimmed[4] | 0x20) == 'e')
                {
                    value = false;
                    return true;
                }

                break;
        }

        value = false;
        return false;
    }

    private static bool TryDecodeHexByteaUtf8(ReadOnlySpan<byte> raw, out SqlValue value)
    {
        value = SqlValue.Null;
        if (raw.Length < 2 || raw[0] != (byte)'\\' || raw[1] != (byte)'x' || (raw.Length & 1) != 0)
        {
            if (raw.IndexOf((byte)0) >= 0 || !Utf8.IsValid(raw))
            {
                return false;
            }

            value = SqlValue.FromBlob(raw.ToArray());
            return true;
        }

        var hex = raw[2..];
        var bytes = new byte[hex.Length >> 1];
        var nibbles = HexNibbles;
        for (var index = 0; index < bytes.Length; index++)
        {
            int hi = nibbles[hex[index << 1]];
            int lo = nibbles[hex[(index << 1) + 1]];
            if ((hi | lo) < 0)
            {
                return false;
            }

            bytes[index] = (byte)((hi << 4) | lo);
        }

        value = SqlValue.FromBlob(bytes);
        return true;
    }

    private static bool TryDecodeUuidUtf8(ReadOnlySpan<byte> raw, out SqlValue value)
    {
        value = SqlValue.Null;
        var trimmed = TrimAsciiWhitespace(raw);
        if (trimmed.Length == 36 && trimmed[8] == (byte)'-' && trimmed[13] == (byte)'-' && trimmed[18] == (byte)'-' && trimmed[23] == (byte)'-')
        {
            var nibbles = HexNibbles;
            var hasUpper = false;
            var valid = true;
            for (var index = 0; index < 36; index++)
            {
                if (index is 8 or 13 or 18 or 23)
                {
                    continue;
                }

                var current = trimmed[index];
                if (nibbles[current] < 0)
                {
                    valid = false;
                    break;
                }

                if (current is >= (byte)'A' and <= (byte)'F')
                {
                    hasUpper = true;
                }
            }

            if (valid)
            {
                if (!hasUpper)
                {
                    value = SqlValue.FromText(Encoding.ASCII.GetString(trimmed));
                    return true;
                }

                Span<char> lowered = stackalloc char[36];
                for (var index = 0; index < 36; index++)
                {
                    var current = trimmed[index];
                    lowered[index] = (char)(current is >= (byte)'A' and <= (byte)'F' ? current | 0x20 : current);
                }

                value = SqlValue.FromText(new string(lowered));
                return true;
            }
        }

        if (!Utf8.IsValid(trimmed) || !Guid.TryParse(Encoding.UTF8.GetString(trimmed), out var uuid))
        {
            return false;
        }

        Span<char> formatted = stackalloc char[36];
        uuid.TryFormat(formatted, out _, "D");
        value = SqlValue.FromText(new string(formatted));
        return true;
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> raw)
    {
        var start = 0;
        var end = raw.Length;
        while (start < end && raw[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\v')
        {
            start++;
        }

        while (end > start && raw[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\v')
        {
            end--;
        }

        return raw[start..end];
    }

    private static bool TryWriteTextValue(PgMessageBuffer output, string text, int oid)
    {
        switch (oid)
        {
            case PgOid.Bool when TryParseBool(text, out var flag):
                output.WriteByte(flag ? (byte)'t' : (byte)'f');
                return true;
            case PgOid.Timestamptz when PgTemporal.IsCanonicalTimestamptz(text):
                output.WriteUtf8(text);
                return true;
            case PgOid.Date or PgOid.Time or PgOid.Timestamp or PgOid.Timestamptz when PgTemporal.TryParse(oid, text, out var instant):
                PgTemporal.WriteText(oid, instant, output);
                return true;
            default:
                output.WriteUtf8(text);
                return true;
        }
    }

    private static bool TryWriteBinary(PgMessageBuffer output, SqlValue value, int oid)
    {
        switch (oid)
        {
            case PgOid.Text or PgOid.Varchar or PgOid.Bpchar or PgOid.Json or PgOid.Unknown:
                return TryWriteText(output, value, oid);
            case PgOid.Jsonb:
                output.WriteByte(1);
                return TryWriteText(output, value, oid);
            case PgOid.Bytea:
                if (value.TryGetBlob(out var blob))
                {
                    output.WriteBytes(blob.Span);
                    return true;
                }

                return value.TryGetText(out var byteaText) && WriteUtf8(output, byteaText);
            case PgOid.Bool:
                if (!TryBool(value, out var flag))
                {
                    return false;
                }

                output.WriteByte(flag ? (byte)1 : (byte)0);
                return true;
            case PgOid.Int2:
                if (!TryIntegral(value, out var shortValue) || shortValue is < short.MinValue or > short.MaxValue)
                {
                    return false;
                }

                output.WriteInt16((short)shortValue);
                return true;
            case PgOid.Int4:
                if (!TryIntegral(value, out var intValue) || intValue is < int.MinValue or > int.MaxValue)
                {
                    return false;
                }

                output.WriteInt32((int)intValue);
                return true;
            case PgOid.Oid:
                if (!TryIntegral(value, out var oidValue) || oidValue is < 0 or > uint.MaxValue)
                {
                    return false;
                }

                output.WriteInt32(unchecked((int)(uint)oidValue));
                return true;
            case PgOid.Int8:
                if (!TryIntegral(value, out var longValue))
                {
                    return false;
                }

                output.WriteInt64(longValue);
                return true;
            case PgOid.Float4:
                if (!TryReal(value, out var single))
                {
                    return false;
                }

                BinaryPrimitives.WriteSingleBigEndian(output.GetSpan(4), (float)single);
                output.Advance(4);
                return true;
            case PgOid.Float8:
                if (!TryReal(value, out var real))
                {
                    return false;
                }

                BinaryPrimitives.WriteDoubleBigEndian(output.GetSpan(8), real);
                output.Advance(8);
                return true;
            case PgOid.Numeric:
                return TryWriteNumericBinary(output, value);
            case PgOid.Uuid:
                return TryWriteUuid(output, value);
            case PgOid.Date or PgOid.Time or PgOid.Timestamp or PgOid.Timestamptz:
                if (!value.TryGetText(out var temporal) || !PgTemporal.TryParse(oid, temporal, out var instant))
                {
                    return false;
                }

                PgTemporal.WriteBinary(oid, instant, output);
                return true;
            default:
                return false;
        }
    }

    private static bool TryWriteNumericBinary(PgMessageBuffer output, SqlValue value)
    {
        if (value.TryGetText(out var text))
        {
            return PgNumeric.TryEncode(text, output);
        }

        if (value.TryGetIntegral(out var integral))
        {
            Span<char> chars = stackalloc char[24];
            return integral.TryFormat(chars, out var written, provider: CultureInfo.InvariantCulture) && PgNumeric.TryEncode(chars[..written], output);
        }

        if (value.TryGetReal(out var real))
        {
            return DecimalText(real) is { } numeric && PgNumeric.TryEncode(numeric, output);
        }

        return false;
    }

    private static bool TryDecodeBinary(int oid, ReadOnlySpan<byte> raw, byte[]? ownedRaw, out SqlValue value)
    {
        value = SqlValue.Null;
        switch (oid, raw.Length)
        {
            case (PgOid.Bool, 1):
                value = SqlValue.FromIntegral(raw[0] == 0 ? 0 : 1);
                return true;
            case (PgOid.Int2, 2):
                value = SqlValue.FromIntegral(BinaryPrimitives.ReadInt16BigEndian(raw));
                return true;
            case (PgOid.Int4, 4):
                value = SqlValue.FromIntegral(BinaryPrimitives.ReadInt32BigEndian(raw));
                return true;
            case (PgOid.Oid, 4):
                value = SqlValue.FromIntegral(BinaryPrimitives.ReadUInt32BigEndian(raw));
                return true;
            case (PgOid.Int8, 8):
                value = SqlValue.FromIntegral(BinaryPrimitives.ReadInt64BigEndian(raw));
                return true;
            case (PgOid.Float4, 4):
                return TryReal(BinaryPrimitives.ReadSingleBigEndian(raw), out value);
            case (PgOid.Float8, 8):
                return TryReal(BinaryPrimitives.ReadDoubleBigEndian(raw), out value);
            case (PgOid.Uuid, 16):
                value = SqlValue.FromText(FormatUuidBigEndian(raw));
                return true;
            case (PgOid.Numeric, _):
                if (!PgNumeric.TryDecode(raw, out var numeric))
                {
                    return false;
                }

                value = NumericValue(numeric);
                return true;
            case (PgOid.Date or PgOid.Time or PgOid.Timestamp or PgOid.Timestamptz, _):
                if (!PgTemporal.TryReadBinary(oid, raw, out var temporal))
                {
                    return false;
                }

                value = SqlValue.FromText(temporal);
                return true;
            case (PgOid.Bytea, _):
                value = SqlValue.FromBlob(ownedRaw ?? raw.ToArray());
                return true;
            case (PgOid.Jsonb, > 0) when raw[0] == 1 && TryDecodeUtf8String(raw[1..], out var jsonbText):
                value = SqlValue.FromText(jsonbText);
                return true;
            case (0 or PgOid.Text or PgOid.Varchar or PgOid.Bpchar or PgOid.Json or PgOid.Unknown, _) when TryDecodeUtf8String(raw, out var textValue):
                value = SqlValue.FromText(textValue);
                return true;
            default:
                return PgOid.TryGetArrayElement(oid, out var element) && TryDecodeBinaryArray(element, raw, out value);
        }
    }

    private static string FormatUuidBigEndian(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[36];
        var hex = HexDigits;
        var pos = 0;
        for (var index = 0; index < 16; index++)
        {
            if (index is 4 or 6 or 8 or 10)
            {
                chars[pos++] = '-';
            }

            var current = bytes[index];
            chars[pos++] = (char)hex[current >> 4];
            chars[pos++] = (char)hex[current & 0xF];
        }

        return new string(chars);
    }

    private static bool TryDecodeBinaryArray(int element, ReadOnlySpan<byte> span, out SqlValue value)
    {
        value = SqlValue.Null;
        if (span.Length < 12)
        {
            return false;
        }

        var dimensions = BinaryPrimitives.ReadInt32BigEndian(span);
        if (dimensions is < 0 or > 1)
        {
            return false;
        }

        span = span[12..];
        var count = 0;
        if (dimensions == 1)
        {
            if (span.Length < 8)
            {
                return false;
            }

            count = BinaryPrimitives.ReadInt32BigEndian(span);
            span = span[8..];
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            for (var index = 0; index < count; index++)
            {
                if (span.Length < 4)
                {
                    return false;
                }

                var length = BinaryPrimitives.ReadInt32BigEndian(span);
                span = span[4..];
                if (length < 0)
                {
                    json.WriteNullValue();
                    continue;
                }

                if (length > span.Length || !TryDecodeBinary(element, span[..length], ownedRaw: null, out var item))
                {
                    return false;
                }

                WriteJson(json, item);
                if (json.BytesCommitted + json.BytesPending > PostgresLimits.MaxPortalBytes)
                {
                    return false;
                }

                span = span[length..];
            }

            json.WriteEndArray();
        }

        value = SqlValue.FromText(Encoding.UTF8.GetString(buffer.WrittenSpan));
        return true;
    }

    private static bool TryDecodeTextArray(int element, string text, out SqlValue value)
    {
        value = SqlValue.Null;
        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
        {
            return false;
        }

        var body = trimmed[1..^1];
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            var index = 0;
            while (index < body.Length)
            {
                var item = new StringBuilder();
                var quoted = false;
                if (body[index] == '"')
                {
                    quoted = true;
                    index++;
                    while (index < body.Length && body[index] != '"')
                    {
                        if (body[index] == '\\' && index + 1 < body.Length)
                        {
                            index++;
                        }

                        item.Append(body[index]);
                        index++;
                    }

                    if (index >= body.Length)
                    {
                        return false;
                    }

                    index++;
                }
                else
                {
                    while (index < body.Length && body[index] != ',')
                    {
                        item.Append(body[index]);
                        index++;
                    }
                }

                var raw = quoted ? item.ToString() : item.ToString().Trim();
                if (!quoted && raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                {
                    json.WriteNullValue();
                }
                else if (!TryDecodeText(element, raw, out var decoded))
                {
                    return false;
                }
                else
                {
                    WriteJson(json, decoded);
                }

                while (index < body.Length && body[index] != ',')
                {
                    index++;
                }

                index++;
            }

            json.WriteEndArray();
        }

        value = SqlValue.FromText(Encoding.UTF8.GetString(buffer.WrittenSpan));
        return true;
    }

    private static void WriteJson(Utf8JsonWriter json, SqlValue value)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                json.WriteNullValue();
                break;
            case SqlValueKind.Integral when value.TryGetIntegral(out var integral):
                json.WriteNumberValue(integral);
                break;
            case SqlValueKind.Real when value.TryGetReal(out var real):
                json.WriteNumberValue(real);
                break;
            case SqlValueKind.Text when value.TryGetText(out var text):
                json.WriteStringValue(text);
                break;
            case SqlValueKind.Blob when value.TryGetBlob(out var blob):
                json.WriteBase64StringValue(blob.Span);
                break;
            default:
                throw new UnreachableException();
        }
    }

    private static SqlValue UntypedValue(string text)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integral))
        {
            Span<char> roundTrip = stackalloc char[24];
            if (integral.TryFormat(roundTrip, out var written, provider: CultureInfo.InvariantCulture) && roundTrip[..written].SequenceEqual(text))
            {
                return SqlValue.FromIntegral(integral);
            }
        }

        if (text.Contains('.', StringComparison.Ordinal)
            && double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var real)
            && double.IsFinite(real))
        {
            Span<char> roundTrip = stackalloc char[32];
            if (real.TryFormat(roundTrip, out var written, "R", CultureInfo.InvariantCulture) && roundTrip[..written].SequenceEqual(text))
            {
                return SqlValue.FromReal(real);
            }
        }

        return SqlValue.FromText(text);
    }

    private static SqlValue NumericValue(string text) =>
        !text.Contains('.', StringComparison.Ordinal) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
            ? SqlValue.FromIntegral(whole)
            : SqlValue.FromText(text);

    private static bool TryDecodeHexBytea(string text, out SqlValue value)
    {
        value = SqlValue.Null;
        if (!text.StartsWith("\\x", StringComparison.Ordinal) || (text.Length % 2) != 0)
        {
            value = SqlValue.FromBlob(Encoding.UTF8.GetBytes(text));
            return true;
        }

        var bytes = new byte[(text.Length - 2) / 2];
        if (Convert.FromHexString(text.AsSpan(2), bytes, out _, out var written) != OperationStatus.Done || written != bytes.Length)
        {
            return false;
        }

        value = SqlValue.FromBlob(bytes);
        return true;
    }

    private static void WriteRealUtf8(PgMessageBuffer output, double real, int oid)
    {
        var span = output.GetSpan(32);
        int written;
        if (oid == PgOid.Float4)
        {
            ((float)real).TryFormat(span, out written, "R", CultureInfo.InvariantCulture);
            LowercaseExponent(span[..written]);
            output.Advance(written);
            return;
        }

        real.TryFormat(span, out written, "R", CultureInfo.InvariantCulture);
        var formatted = span[..written];
        var expIndex = formatted.IndexOf((byte)'E');
        if (expIndex >= 0)
        {
            if (oid == PgOid.Numeric && Utf8Parser.TryParse(formatted, out decimal exact, out var consumed) && consumed == written
                && exact.TryFormat(span, out var decimalWritten, default, CultureInfo.InvariantCulture))
            {
                output.Advance(decimalWritten);
                return;
            }

            formatted[expIndex] = (byte)'e';
        }

        output.Advance(written);
    }

    private static void LowercaseExponent(Span<byte> span)
    {
        var index = span.IndexOf((byte)'E');
        if (index >= 0)
        {
            span[index] = (byte)'e';
        }
    }

    private static string? NumericText(SqlValue value)
    {
        if (value.TryGetIntegral(out var integral))
        {
            return integral.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetReal(out var real))
        {
            return DecimalText(real);
        }

        return value.TryGetText(out var text) && PgNumeric.IsDecimalText(text) ? text : null;
    }

    private static string? DecimalText(double real)
    {
        var text = real.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('E', StringComparison.Ordinal))
        {
            return text;
        }

        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact) ? exact.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static bool TryWriteUuid(PgMessageBuffer output, SqlValue value)
    {
        if (value.TryGetBlob(out var blob) && blob.Length == 16)
        {
            output.WriteBytes(blob.Span);
            return true;
        }

        if (!value.TryGetText(out var text))
        {
            return false;
        }

        var span = text.AsSpan();
        if (span.Length == 36 && span[8] == '-' && span[13] == '-' && span[18] == '-' && span[23] == '-')
        {
            var dest = output.GetSpan(16);
            var nibbles = HexNibbles;
            var src = 0;
            var valid = true;
            for (var index = 0; index < 16; index++)
            {
                if (index is 4 or 6 or 8 or 10)
                {
                    src++;
                }

                var c0 = span[src++];
                var c1 = span[src++];
                if (c0 >= 128 || c1 >= 128)
                {
                    valid = false;
                    break;
                }

                int hi = nibbles[c0];
                int lo = nibbles[c1];
                if ((hi | lo) < 0)
                {
                    valid = false;
                    break;
                }

                dest[index] = (byte)((hi << 4) | lo);
            }

            if (valid)
            {
                output.Advance(16);
                return true;
            }
        }

        if (!Guid.TryParse(span, out var uuid))
        {
            return false;
        }

        uuid.TryWriteBytes(output.GetSpan(16), bigEndian: true, out _);
        output.Advance(16);
        return true;
    }

    private static bool TryBool(SqlValue value, out bool flag)
    {
        flag = false;
        if (value.TryGetIntegral(out var integral))
        {
            flag = integral != 0;
            return true;
        }

        if (value.TryGetReal(out var real))
        {
            flag = real != 0;
            return true;
        }

        return value.TryGetText(out var text) && TryParseBool(text, out flag);
    }

    private static bool TryIntegral(SqlValue value, out long integral)
    {
        integral = 0;
        if (value.TryGetIntegral(out integral))
        {
            return true;
        }

        if (value.TryGetReal(out var real))
        {
            if (real != Math.Truncate(real) || Math.Abs(real) >= 9.2e18)
            {
                return false;
            }

            integral = (long)real;
            return true;
        }

        return value.TryGetText(out var text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integral);
    }

    private static bool TryReal(SqlValue value, out double real)
    {
        real = 0;
        if (value.TryGetReal(out real))
        {
            return true;
        }

        if (value.TryGetIntegral(out var integral))
        {
            real = integral;
            return true;
        }

        return value.TryGetText(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out real);
    }

    private static bool TryReal(double real, out SqlValue value)
    {
        value = double.IsFinite(real) ? SqlValue.FromReal(real) : SqlValue.Null;
        return double.IsFinite(real);
    }

    private static bool WriteUtf8(PgMessageBuffer output, string text)
    {
        output.WriteUtf8(text);
        return true;
    }

    private static void WriteHex(PgMessageBuffer output, ReadOnlySpan<byte> bytes)
    {
        var span = output.GetSpan(bytes.Length * 2);
        var hex = HexDigits;
        for (var index = 0; index < bytes.Length; index++)
        {
            var current = bytes[index];
            span[index << 1] = hex[current >> 4];
            span[(index << 1) + 1] = hex[current & 0xF];
        }

        output.Advance(bytes.Length * 2);
    }
}
