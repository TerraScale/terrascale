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

    public static bool SupportsBinary(int oid) => PgOid.IsKnown(oid) && oid != PgOid.Unknown;

    public static short ResultFormat(IReadOnlyList<short> formats, int index, int oid)
    {
        var requested = formats.Count == 0 ? (short)TextFormatCode : formats[Math.Min(index, formats.Count - 1)];
        return requested == BinaryFormatCode && SupportsBinary(oid) ? (short)BinaryFormatCode : (short)TextFormatCode;
    }

    public static bool TryWrite(PgMessageBuffer output, SqlValue value, int oid, bool binary)
    {
        if (value.Kind == SqlValueKind.Null)
        {
            output.WriteInt32(-1);
            return true;
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
                output.WriteUtf8(FormatReal(real, oid));
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

        if (format == BinaryFormatCode)
        {
            return TryDecodeBinary(oid, raw, out value);
        }

        if (!Utf8.IsValid(raw))
        {
            return false;
        }

        return TryDecodeText(oid, Encoding.UTF8.GetString(raw), out value);
    }

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
                value = NumericValue(text);
                return PgNumeric.IsDecimalText(text);
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
        long total = ObjectOverhead;
        foreach (var row in rows)
        {
            total += ObjectOverhead + IntPtr.Size;
            foreach (var value in row)
            {
                total += Footprint(value);
            }
        }

        return total;
    }

    public static bool TryParseBool(ReadOnlySpan<char> text, out bool value)
    {
        var trimmed = text.Trim();
        foreach (var accepted in (ReadOnlySpan<string>)["t", "true", "y", "yes", "on", "1"])
        {
            if (trimmed.Equals(accepted, StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }
        }

        foreach (var accepted in (ReadOnlySpan<string>)["f", "false", "n", "no", "off", "0"])
        {
            if (trimmed.Equals(accepted, StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }
        }

        value = false;
        return false;
    }

    private static bool TryWriteTextValue(PgMessageBuffer output, string text, int oid)
    {
        switch (oid)
        {
            case PgOid.Bool when TryParseBool(text, out var flag):
                output.WriteByte(flag ? (byte)'t' : (byte)'f');
                return true;
            case PgOid.Date or PgOid.Time or PgOid.Timestamp or PgOid.Timestamptz when PgTemporal.TryParse(oid, text, out var instant):
                output.WriteUtf8(PgTemporal.Format(oid, instant));
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
                return NumericText(value) is { } numeric && PgNumeric.TryEncode(numeric, output);
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

    private static bool TryDecodeBinary(int oid, byte[] raw, out SqlValue value)
    {
        value = SqlValue.Null;
        if (PgOid.TryGetArrayElement(oid, out var element))
        {
            return TryDecodeBinaryArray(element, raw, out value);
        }

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
                value = SqlValue.FromText(new Guid(raw, bigEndian: true).ToString("D", CultureInfo.InvariantCulture));
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
                value = SqlValue.FromBlob(raw);
                return true;
            case (PgOid.Jsonb, > 0) when raw[0] == 1 && Utf8.IsValid(raw.AsSpan(1)):
                value = SqlValue.FromText(Encoding.UTF8.GetString(raw.AsSpan(1)));
                return true;
            case (0 or PgOid.Text or PgOid.Varchar or PgOid.Bpchar or PgOid.Json or PgOid.Unknown, _) when Utf8.IsValid(raw):
                value = SqlValue.FromText(Encoding.UTF8.GetString(raw));
                return true;
            default:
                return false;
        }
    }

    private static bool TryDecodeBinaryArray(int element, byte[] raw, out SqlValue value)
    {
        value = SqlValue.Null;
        ReadOnlySpan<byte> span = raw;
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

                if (length > span.Length || !TryDecodeBinary(element, span[..length].ToArray(), out var item))
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
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integral)
            && integral.ToString(CultureInfo.InvariantCulture) == text)
        {
            return SqlValue.FromIntegral(integral);
        }

        if (text.Contains('.', StringComparison.Ordinal)
            && double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var real)
            && double.IsFinite(real)
            && real.ToString("R", CultureInfo.InvariantCulture) == text)
        {
            return SqlValue.FromReal(real);
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

    private static string FormatReal(double real, int oid)
    {
        if (oid == PgOid.Numeric && DecimalText(real) is { } numeric)
        {
            return numeric;
        }

        if (oid == PgOid.Float4)
        {
            return ((float)real).ToString("R", CultureInfo.InvariantCulture).Replace("E", "e", StringComparison.Ordinal);
        }

        return real.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e", StringComparison.Ordinal);
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

        if (!value.TryGetText(out var text) || !Guid.TryParse(text, out var uuid))
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
        for (var index = 0; index < bytes.Length; index++)
        {
            span[2 * index] = HexDigits[bytes[index] >> 4];
            span[(2 * index) + 1] = HexDigits[bytes[index] & 0xF];
        }

        output.Advance(bytes.Length * 2);
    }
}
