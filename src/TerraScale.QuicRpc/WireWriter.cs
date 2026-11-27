using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace TerraScale.QuicRpc;

public static class WireWriter
{
    extension(IBufferWriter<byte> writer)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt8(byte value)
        {
            writer.GetSpan(1)[0] = value;
            writer.Advance(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBool(bool value) => writer.WriteUInt8(value ? (byte)1 : (byte)0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(writer.GetSpan(sizeof(ushort)), value);
            writer.Advance(sizeof(ushort));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(sizeof(int)), value);
            writer.Advance(sizeof(int));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(writer.GetSpan(sizeof(uint)), value);
            writer.Advance(sizeof(uint));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(sizeof(long)), value);
            writer.Advance(sizeof(long));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(writer.GetSpan(sizeof(ulong)), value);
            writer.Advance(sizeof(ulong));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteDouble(double value)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(writer.GetSpan(sizeof(double)), value);
            writer.Advance(sizeof(double));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarUInt64(ulong value)
        {
            if (value < 0x80)
            {
                writer.GetSpan(1)[0] = (byte)value;
                writer.Advance(1);
                return;
            }

            WriteVarUInt64MultiByte(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarInt64(long value) => writer.WriteVarUInt64((ulong)((value << 1) ^ (value >> 63)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUlid(Ulid value)
        {
            if (!value.TryWriteBytes(writer.GetSpan(16)))
            {
                throw new UnreachableException();
            }

            writer.Advance(16);
        }

        public void WriteString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var charLength = value.Length;
            if (charLength == 0)
            {
                writer.WriteUInt8(0);
                return;
            }

            if (charLength < 0x80)
            {
                var span = writer.GetSpan(1 + charLength);
                if (Ascii.FromUtf16(value, span.Slice(1, charLength), out var written) == OperationStatus.Done)
                {
                    span[0] = (byte)written;
                    writer.Advance(1 + written);
                    return;
                }
            }

            var byteCount = Encoding.UTF8.GetByteCount(value);
            writer.WriteVarUInt64((ulong)byteCount);
            if (byteCount > 0)
            {
                writer.Advance(Encoding.UTF8.GetBytes(value.AsSpan(), writer.GetSpan(byteCount)));
            }
        }

        public void WriteNullableString(string? value)
        {
            if (value is null)
            {
                writer.WriteUInt8(0);
                return;
            }

            var charLength = value.Length;
            if (charLength == 0)
            {
                writer.WriteUInt8(1);
                return;
            }

            if (charLength < 0x7F)
            {
                var span = writer.GetSpan(1 + charLength);
                if (Ascii.FromUtf16(value, span.Slice(1, charLength), out var written) == OperationStatus.Done)
                {
                    span[0] = (byte)(written + 1);
                    writer.Advance(1 + written);
                    return;
                }
            }

            var byteCount = Encoding.UTF8.GetByteCount(value);
            writer.WriteVarUInt64((ulong)byteCount + 1);
            if (byteCount > 0)
            {
                writer.Advance(Encoding.UTF8.GetBytes(value.AsSpan(), writer.GetSpan(byteCount)));
            }
        }

        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            var length = value.Length;
            if (length == 0)
            {
                writer.WriteUInt8(0);
                return;
            }

            var prefixSize = VarUIntSize((ulong)length);
            var span = writer.GetSpan(prefixSize + length);
            WriteVarUIntUnchecked(span, (ulong)length);
            value.CopyTo(span[prefixSize..]);
            writer.Advance(prefixSize + length);
        }

        public void WriteStringList(IReadOnlyList<string> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var count = values.Count;
            writer.WriteVarUInt64((ulong)count);
            for (var index = 0; index < count; index++)
            {
                writer.WriteString(values[index]);
            }
        }
    }

    private static void WriteVarUInt64MultiByte(IBufferWriter<byte> writer, ulong value)
    {
        var span = writer.GetSpan(10);
        var count = 0;
        while (value >= 0x80)
        {
            span[count++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[count++] = (byte)value;
        writer.Advance(count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int VarUIntSize(ulong value)
    {
        if (value < 1UL << 7)
        {
            return 1;
        }

        if (value < 1UL << 14)
        {
            return 2;
        }

        if (value < 1UL << 21)
        {
            return 3;
        }

        if (value < 1UL << 28)
        {
            return 4;
        }

        return 5;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteVarUIntUnchecked(Span<byte> destination, ulong value)
    {
        var index = 0;
        while (value >= 0x80)
        {
            destination[index++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[index] = (byte)value;
    }
}
