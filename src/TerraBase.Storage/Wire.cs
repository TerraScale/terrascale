using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Text;

namespace TerraBase.Storage;

internal static class WireWriter
{
    public static void Byte(IBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    public static void VarUInt(IBufferWriter<byte> writer, ulong value)
    {
        var span = writer.GetSpan(10);
        var index = 0;
        while (value >= 0x80)
        {
            span[index++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[index++] = (byte)value;
        writer.Advance(index);
    }

    public static void VarInt(IBufferWriter<byte> writer, long value) =>
        VarUInt(writer, (ulong)((value << 1) ^ (value >> 63)));

    public static void UInt16(IBufferWriter<byte> writer, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(writer.GetSpan(2), value);
        writer.Advance(2);
    }

    public static void Double(IBufferWriter<byte> writer, double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(writer.GetSpan(8), value);
        writer.Advance(8);
    }

    public static void Bytes(IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes)
    {
        VarUInt(writer, (ulong)bytes.Length);
        writer.Write(bytes);
    }

    public static void String(IBufferWriter<byte> writer, string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        VarUInt(writer, (ulong)count);
        if (count == 0)
        {
            return;
        }

        writer.Advance(Encoding.UTF8.GetBytes(value, writer.GetSpan(count)));
    }

    public static void Hlc(IBufferWriter<byte> writer, HlcTimestamp hlc)
    {
        HlcCodec.Write(writer.GetSpan(HlcTimestamp.EncodedSize), hlc);
        writer.Advance(HlcTimestamp.EncodedSize);
    }

    public static void Value(IBufferWriter<byte> writer, SqlValue value)
    {
        Byte(writer, (byte)value.Kind);
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                break;
            case SqlValueKind.Integral:
                _ = value.TryGetIntegral(out var integral);
                VarInt(writer, integral);
                break;
            case SqlValueKind.Real:
                _ = value.TryGetReal(out var real);
                Double(writer, real);
                break;
            case SqlValueKind.Text:
                _ = value.TryGetText(out var text);
                String(writer, text ?? throw new UnreachableException());
                break;
            case SqlValueKind.Blob:
                _ = value.TryGetBlob(out var blob);
                Bytes(writer, blob.Span);
                break;
            default:
                throw new UnreachableException();
        }
    }
}

internal ref struct WireReader(ReadOnlySpan<byte> source)
{
    private ReadOnlySpan<byte> _source = source;

    public bool Failed { get; private set; }

    public readonly bool End => _source.IsEmpty;

    public byte Byte()
    {
        if (Failed || _source.IsEmpty)
        {
            Failed = true;
            return 0;
        }

        var value = _source[0];
        _source = _source[1..];
        return value;
    }

    public ulong VarUInt()
    {
        ulong result = 0;
        for (var index = 0; index < 10 && !Failed; index++)
        {
            if (index >= _source.Length)
            {
                break;
            }

            var current = _source[index];
            if (index == 9 && current > 1)
            {
                break;
            }

            result |= (ulong)(current & 0x7F) << (7 * index);
            if (current < 0x80)
            {
                _source = _source[(index + 1)..];
                return result;
            }
        }

        Failed = true;
        return 0;
    }

    public long VarInt()
    {
        var raw = VarUInt();
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    public int Count()
    {
        var raw = VarUInt();
        if (raw > (ulong)_source.Length)
        {
            Failed = true;
            return 0;
        }

        return (int)raw;
    }

    public ushort UInt16()
    {
        if (Failed || _source.Length < 2)
        {
            Failed = true;
            return 0;
        }

        var value = BinaryPrimitives.ReadUInt16LittleEndian(_source);
        _source = _source[2..];
        return value;
    }

    public double Double()
    {
        if (Failed || _source.Length < 8)
        {
            Failed = true;
            return 0;
        }

        var value = BinaryPrimitives.ReadDoubleLittleEndian(_source);
        _source = _source[8..];
        return value;
    }

    public ReadOnlySpan<byte> Bytes()
    {
        var length = Count();
        return Take(length);
    }

    public ReadOnlySpan<byte> Take(int length)
    {
        if (Failed || length < 0 || length > _source.Length)
        {
            Failed = true;
            return [];
        }

        var bytes = _source[..length];
        _source = _source[length..];
        return bytes;
    }

    public string String()
    {
        var bytes = Bytes();
        return Failed ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    public HlcTimestamp Hlc()
    {
        var bytes = Take(HlcTimestamp.EncodedSize);
        return Failed ? HlcTimestamp.Zero : HlcCodec.Read(bytes);
    }

    public SqlValue Value()
    {
        var kind = Byte();
        switch (kind)
        {
            case (byte)SqlValueKind.Null:
                return SqlValue.Null;
            case (byte)SqlValueKind.Integral:
                return SqlValue.FromIntegral(VarInt());
            case (byte)SqlValueKind.Real:
                var real = Double();
                if (!double.IsFinite(real))
                {
                    Failed = true;
                    return SqlValue.Null;
                }

                return SqlValue.FromReal(real);
            case (byte)SqlValueKind.Text:
                return SqlValue.FromText(String());
            case (byte)SqlValueKind.Blob:
                return SqlValue.FromBlob(Bytes().ToArray());
            default:
                Failed = true;
                return SqlValue.Null;
        }
    }
}

internal static class HlcCodec
{
    public const long MaxWallMs = (1L << 48) - 1;

    public static void Write(Span<byte> destination, HlcTimestamp hlc)
    {
        var wall = (ulong)Math.Clamp(hlc.WallMs, 0, MaxWallMs);
        destination[0] = (byte)(wall >> 40);
        destination[1] = (byte)(wall >> 32);
        destination[2] = (byte)(wall >> 24);
        destination[3] = (byte)(wall >> 16);
        destination[4] = (byte)(wall >> 8);
        destination[5] = (byte)wall;
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], hlc.Logical);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], hlc.NodeId);
    }

    public static HlcTimestamp Read(ReadOnlySpan<byte> source)
    {
        var wall = ((long)source[0] << 40)
            | ((long)source[1] << 32)
            | ((long)source[2] << 24)
            | ((long)source[3] << 16)
            | ((long)source[4] << 8)
            | source[5];
        return new HlcTimestamp(wall, BinaryPrimitives.ReadUInt16BigEndian(source[6..]), BinaryPrimitives.ReadUInt16BigEndian(source[8..]));
    }

    public static HlcTimestamp Max(HlcTimestamp left, HlcTimestamp right) => left >= right ? left : right;
}

internal static class PrimaryKeyCodec
{
    public static byte[] Encode(ReadOnlySpan<SqlValue> values)
    {
        var writer = new ArrayBufferWriter<byte>(16 + (values.Length * 12));
        WireWriter.VarUInt(writer, (ulong)values.Length);
        foreach (var value in values)
        {
            WireWriter.Value(writer, value);
        }

        return writer.WrittenSpan.ToArray();
    }

    public static string ToKeyString(ReadOnlySpan<byte> encoded) => Base64Url.EncodeToString(encoded);

    public static bool TryDecodeKeyString(string key, out byte[] encoded)
    {
        encoded = [];
        if (key.Length == 0)
        {
            return false;
        }

        var buffer = new byte[Base64Url.GetMaxDecodedLength(key.Length)];
        if (Base64Url.DecodeFromChars(key, buffer, out _, out var written) != OperationStatus.Done)
        {
            return false;
        }

        encoded = buffer[..written];
        return true;
    }
}
