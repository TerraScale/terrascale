using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _buffer.AsSpan(_length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Begin(char type)
    {
        var start = _length;
        Ensure(5);
        _buffer[_length] = (byte)type;
        _messageStart = _length + 1;
        BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_messageStart), 0);
        _length += 5;
        return start;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReserveInt32()
    {
        var offset = _length;
        WriteInt32(0);
        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchInt32(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(offset), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt16(short value)
    {
        Ensure(2);
        BinaryPrimitives.WriteInt16BigEndian(_buffer.AsSpan(_length), value);
        _length += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt32(int value)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt64(long value)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64BigEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    public void WriteUtf8(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        var maxBytes = value.Length * 3;
        if (maxBytes <= 1024 || _buffer.Length - _length >= maxBytes)
        {
            Ensure(maxBytes);
            _length += Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
            return;
        }

        var count = Encoding.UTF8.GetByteCount(value);
        Ensure(count);
        _length += Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
    }

    public void WriteCString(ReadOnlySpan<char> value)
    {
        if (!value.IsEmpty)
        {
            var maxBytes = (value.Length * 3) + 1;
            if (maxBytes <= 1024 || _buffer.Length - _length >= maxBytes)
            {
                Ensure(maxBytes);
                _length += Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
                _buffer[_length++] = 0;
                return;
            }

            var count = Encoding.UTF8.GetByteCount(value);
            Ensure(count + 1);
            _length += Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
            _buffer[_length++] = 0;
            return;
        }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int size)
    {
        if (_buffer.Length - _length >= size)
        {
            return;
        }

        Grow(size);
    }

    private void Grow(int size)
    {
        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + size));
        _buffer.AsSpan(0, _length).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }
}
