using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace TerraScale.QuicRpc;

public static class WireWriter
{
    extension(IBufferWriter<byte> writer)
    {
        public void WriteUInt8(byte value)
        {
            writer.GetSpan(1)[0] = value;
            writer.Advance(1);
        }

        public void WriteBool(bool value) => writer.WriteUInt8(value ? (byte)1 : (byte)0);

        public void WriteUInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(writer.GetSpan(sizeof(ushort)), value);
            writer.Advance(sizeof(ushort));
        }

        public void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(sizeof(int)), value);
            writer.Advance(sizeof(int));
        }

        public void WriteUInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(writer.GetSpan(sizeof(uint)), value);
            writer.Advance(sizeof(uint));
        }

        public void WriteInt64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(sizeof(long)), value);
            writer.Advance(sizeof(long));
        }

        public void WriteUInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(writer.GetSpan(sizeof(ulong)), value);
            writer.Advance(sizeof(ulong));
        }

        public void WriteDouble(double value)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(writer.GetSpan(sizeof(double)), value);
            writer.Advance(sizeof(double));
        }

        public void WriteVarUInt64(ulong value)
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

        public void WriteVarInt64(long value) => writer.WriteVarUInt64((ulong)((value << 1) ^ (value >> 63)));

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
            var byteCount = Encoding.UTF8.GetByteCount(value);
            writer.WriteVarUInt64((ulong)byteCount);
            if (byteCount > 0)
            {
                writer.Advance(Encoding.UTF8.GetBytes(value, writer.GetSpan(byteCount)));
            }
        }

        public void WriteNullableString(string? value)
        {
            if (value is null)
            {
                writer.WriteVarUInt64(0);
                return;
            }

            var byteCount = Encoding.UTF8.GetByteCount(value);
            writer.WriteVarUInt64((ulong)byteCount + 1);
            if (byteCount > 0)
            {
                writer.Advance(Encoding.UTF8.GetBytes(value, writer.GetSpan(byteCount)));
            }
        }

        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            writer.WriteVarUInt64((ulong)value.Length);
            if (!value.IsEmpty)
            {
                value.CopyTo(writer.GetSpan(value.Length));
                writer.Advance(value.Length);
            }
        }

        public void WriteStringList(IReadOnlyList<string> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            writer.WriteVarUInt64((ulong)values.Count);
            foreach (var value in values)
            {
                writer.WriteString(value);
            }
        }
    }
}
