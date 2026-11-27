using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace TerraBase.Storage;

internal ref struct PooledBufferWriter : IDisposable
{
    private byte[] _buffer;
    private int _written;

    public PooledBufferWriter(int initialCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 64));
        _written = 0;
    }

    public readonly ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public readonly int WrittenCount => _written;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Byte(byte value)
    {
        Ensure(1);
        _buffer[_written++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void VarUInt(ulong value)
    {
        if (value < 0x80)
        {
            Ensure(1);
            _buffer[_written++] = (byte)value;
            return;
        }

        Ensure(10);
        var written = _written;
        var buffer = _buffer;
        while (value >= 0x80)
        {
            buffer[written++] = (byte)(value | 0x80);
            value >>= 7;
        }

        buffer[written++] = (byte)value;
        _written = written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void VarInt(long value) =>
        VarUInt((ulong)((value << 1) ^ (value >> 63)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UInt16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_written), value);
        _written += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Double(double value)
    {
        Ensure(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_written), value);
        _written += 8;
    }

    public void Bytes(ReadOnlySpan<byte> bytes)
    {
        VarUInt((ulong)bytes.Length);
        if (bytes.IsEmpty)
        {
            return;
        }

        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_written));
        _written += bytes.Length;
    }

    public void String(string value)
    {
        if (value.Length == 0)
        {
            Byte(0);
            return;
        }

        var count = Encoding.UTF8.GetByteCount(value);
        VarUInt((ulong)count);
        Ensure(count);
        _written += Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_written, count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Hlc(HlcTimestamp hlc)
    {
        Ensure(HlcTimestamp.EncodedSize);
        HlcCodec.Write(_buffer.AsSpan(_written, HlcTimestamp.EncodedSize), hlc);
        _written += HlcTimestamp.EncodedSize;
    }

    public void Value(SqlValue value)
    {
        Byte((byte)value.Kind);
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                break;
            case SqlValueKind.Integral:
                _ = value.TryGetIntegral(out var integral);
                VarInt(integral);
                break;
            case SqlValueKind.Real:
                _ = value.TryGetReal(out var real);
                Double(real);
                break;
            case SqlValueKind.Text:
                _ = value.TryGetText(out var text);
                String(text ?? throw new UnreachableException());
                break;
            case SqlValueKind.Blob:
                _ = value.TryGetBlob(out var blob);
                Bytes(blob.Span);
                break;
            default:
                throw new UnreachableException();
        }
    }

    public readonly byte[] ToArray() => _written == 0 ? [] : _buffer.AsSpan(0, _written).ToArray();

    public void Dispose()
    {
        var buffer = _buffer;
        if (buffer.Length > 0)
        {
            _buffer = [];
            _written = 0;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int sizeHint)
    {
        if (_buffer.Length - _written < sizeHint)
        {
            Grow(sizeHint);
        }
    }

    private void Grow(int sizeHint)
    {
        var next = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _written + sizeHint));
        _buffer.AsSpan(0, _written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }
}

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
        writer.Advance(WriteVarUInt(span, value));
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
        if (value.Length == 0)
        {
            Byte(writer, 0);
            return;
        }

        var count = Encoding.UTF8.GetByteCount(value);
        VarUInt(writer, (ulong)count);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteVarUInt(Span<byte> destination, ulong value)
    {
        if (value < 0x80)
        {
            destination[0] = (byte)value;
            return 1;
        }

        var index = 0;
        while (value >= 0x80)
        {
            destination[index++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[index++] = (byte)value;
        return index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteVarInt(Span<byte> destination, long value) =>
        WriteVarUInt(destination, (ulong)((value << 1) ^ (value >> 63)));
}

internal ref struct WireReader(ReadOnlySpan<byte> source)
{
    private ReadOnlySpan<byte> _source = source;

    public bool Failed { get; private set; }

    public readonly bool End => _source.IsEmpty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong VarUInt()
    {
        if (Failed || _source.IsEmpty)
        {
            Failed = true;
            return 0;
        }

        var first = _source[0];
        if (first < 0x80)
        {
            _source = _source[1..];
            return first;
        }

        return VarUIntMultiByte();
    }

    private ulong VarUIntMultiByte()
    {
        ulong result = 0;
        for (var index = 0; index < 10; index++)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long VarInt()
    {
        var raw = VarUInt();
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> Bytes()
    {
        var length = Count();
        return Take(length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        return Failed || bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
                var bytes = Bytes();
                return SqlValue.FromBlob(bytes.IsEmpty ? ReadOnlyMemory<byte>.Empty : bytes.ToArray());
            default:
                Failed = true;
                return SqlValue.Null;
        }
    }
}

internal static class HlcCodec
{
    public const long MaxWallMs = (1L << 48) - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HlcTimestamp Max(HlcTimestamp left, HlcTimestamp right) => left >= right ? left : right;
}

internal static class PrimaryKeyCodec
{
    public static bool TryEncode(ReadOnlySpan<SqlValue> values, Span<byte> destination, out int written)
    {
        if (destination.Length < 10)
        {
            written = 0;
            return false;
        }

        var offset = WireWriter.WriteVarUInt(destination, (ulong)values.Length);
        foreach (var value in values)
        {
            if (offset >= destination.Length)
            {
                written = 0;
                return false;
            }

            destination[offset++] = (byte)value.Kind;
            switch (value.Kind)
            {
                case SqlValueKind.Null:
                    break;
                case SqlValueKind.Integral:
                    if (destination.Length - offset < 10)
                    {
                        written = 0;
                        return false;
                    }

                    _ = value.TryGetIntegral(out var integral);
                    offset += WireWriter.WriteVarInt(destination[offset..], integral);
                    break;
                case SqlValueKind.Real:
                    if (destination.Length - offset < 8)
                    {
                        written = 0;
                        return false;
                    }

                    _ = value.TryGetReal(out var real);
                    BinaryPrimitives.WriteDoubleLittleEndian(destination[offset..], real);
                    offset += 8;
                    break;
                case SqlValueKind.Text:
                    _ = value.TryGetText(out var text);
                    var resolved = text ?? throw new UnreachableException();
                    var utf8Length = resolved.Length == 0 ? 0 : Encoding.UTF8.GetByteCount(resolved);
                    if (destination.Length - offset < 10 + utf8Length)
                    {
                        written = 0;
                        return false;
                    }

                    offset += WireWriter.WriteVarUInt(destination[offset..], (ulong)utf8Length);
                    if (utf8Length > 0)
                    {
                        offset += Encoding.UTF8.GetBytes(resolved, destination.Slice(offset, utf8Length));
                    }

                    break;
                case SqlValueKind.Blob:
                    _ = value.TryGetBlob(out var blob);
                    var span = blob.Span;
                    if (destination.Length - offset < 10 + span.Length)
                    {
                        written = 0;
                        return false;
                    }

                    offset += WireWriter.WriteVarUInt(destination[offset..], (ulong)span.Length);
                    if (!span.IsEmpty)
                    {
                        span.CopyTo(destination[offset..]);
                        offset += span.Length;
                    }

                    break;
                default:
                    throw new UnreachableException();
            }
        }

        written = offset;
        return true;
    }

    public static byte[] Encode(ReadOnlySpan<SqlValue> values)
    {
        Span<byte> stack = stackalloc byte[128];
        if (TryEncode(values, stack, out var written))
        {
            return stack[..written].ToArray();
        }

        var writer = new PooledBufferWriter(128 + (values.Length * 16));
        try
        {
            writer.VarUInt((ulong)values.Length);
            foreach (var value in values)
            {
                writer.Value(value);
            }

            return writer.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    public static string ToKeyString(ReadOnlySpan<byte> encoded) => Base64Url.EncodeToString(encoded);

    public static bool TryDecodeKeyString(string key, out byte[] encoded)
    {
        encoded = [];
        if (key.Length == 0)
        {
            return false;
        }

        var maxLength = Base64Url.GetMaxDecodedLength(key.Length);
        if (maxLength <= 256)
        {
            Span<byte> stack = stackalloc byte[256];
            if (Base64Url.DecodeFromChars(key, stack, out _, out var written) != OperationStatus.Done)
            {
                return false;
            }

            encoded = stack[..written].ToArray();
            return true;
        }

        var rented = ArrayPool<byte>.Shared.Rent(maxLength);
        try
        {
            if (Base64Url.DecodeFromChars(key, rented, out _, out var written) != OperationStatus.Done)
            {
                return false;
            }

            encoded = rented.AsSpan(0, written).ToArray();
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
