using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace TerraBase.Postgres.Protocol;

internal sealed class PgMessageBuffer : IBufferWriter<byte>, IDisposable
{
    private const int InitialSize = 8192;

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialSize);
    private int _length;
    private int _messageStart = -1;

    public int Length => _length;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _length);
        _length += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _buffer.AsMemory(_length);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _buffer.AsSpan(_length);
    }

    public int Begin(char type)
    {
        var start = _length;
        WriteByte((byte)type);
        _messageStart = _length;
        WriteInt32(0);
        return start;
    }

    public void End()
    {
        if (_messageStart < 0)
        {
            throw new UnreachableException("No backend message is open.");
        }

        BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_messageStart), _length - _messageStart);
        _messageStart = -1;
    }

    public void Truncate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _length);
        _length = length;
        _messageStart = -1;
    }

    public int ReserveInt32()
    {
        var offset = _length;
        WriteInt32(0);
        return offset;
    }

    public void PatchInt32(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(offset), value);

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    public void WriteInt16(short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(GetSpan(2), value);
        _length += 2;
    }

    public void WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(GetSpan(4), value);
        _length += 4;
    }

    public void WriteInt64(long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(GetSpan(8), value);
        _length += 8;
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(GetSpan(value.Length));
        _length += value.Length;
    }

    public void WriteUtf8(ReadOnlySpan<char> value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        _length += Encoding.UTF8.GetBytes(value, GetSpan(count));
    }

    public void WriteCString(ReadOnlySpan<char> value)
    {
        WriteUtf8(value);
        WriteByte(0);
    }

    public void Clear()
    {
        _length = 0;
        _messageStart = -1;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _length = 0;
    }

    private void Ensure(int size)
    {
        if (_buffer.Length - _length >= size)
        {
            return;
        }

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + size));
        _buffer.AsSpan(0, _length).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }
}
