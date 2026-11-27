using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace TerraScale.QuicRpc;

public ref struct WireReader(ReadOnlySpan<byte> buffer)
{
    private const int MaxVarIntBytes = 10;

    private readonly ReadOnlySpan<byte> _buffer = buffer;
    private int _position;
    private bool _faulted;

    public readonly int Remaining => _buffer.Length - _position;

    public readonly bool IsFaulted => _faulted;

    public readonly bool IsComplete => !_faulted && _position == _buffer.Length;

    public void MarkFaulted() => _faulted = true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadUInt8()
    {
        var span = Take(1);
        return span.IsEmpty ? (byte)0 : span[0];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBool()
    {
        var value = ReadUInt8();
        if (value > 1)
        {
            _faulted = true;
        }

        return value == 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16()
    {
        var span = Take(sizeof(ushort));
        return span.IsEmpty ? (ushort)0 : BinaryPrimitives.ReadUInt16LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32()
    {
        var span = Take(sizeof(int));
        return span.IsEmpty ? 0 : BinaryPrimitives.ReadInt32LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32()
    {
        var span = Take(sizeof(uint));
        return span.IsEmpty ? 0u : BinaryPrimitives.ReadUInt32LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64()
    {
        var span = Take(sizeof(long));
        return span.IsEmpty ? 0L : BinaryPrimitives.ReadInt64LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64()
    {
        var span = Take(sizeof(ulong));
        return span.IsEmpty ? 0UL : BinaryPrimitives.ReadUInt64LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadDouble()
    {
        var span = Take(sizeof(double));
        return span.IsEmpty ? 0d : BinaryPrimitives.ReadDoubleLittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadVarUInt64()
    {
        var position = _position;
        if (!_faulted && (uint)position < (uint)_buffer.Length)
        {
            var first = _buffer[position];
            if ((first & 0x80) == 0)
            {
                _position = position + 1;
                return first;
            }
        }

        return ReadVarUInt64Slow();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadVarInt64()
    {
        var raw = ReadVarUInt64();
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Ulid ReadUlid()
    {
        var span = Take(16);
        return span.IsEmpty ? default : new Ulid(span);
    }

    public string ReadString()
    {
        var length = ReadLength();
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(Take(length));
    }

    public string? ReadNullableString()
    {
        var marker = ReadVarUInt64();
        if (marker == 0)
        {
            return null;
        }

        var length = CheckedLength(marker - 1);
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(Take(length));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadBytesSpan() => Take(ReadLength());

    public byte[] ReadBytes()
    {
        var span = ReadBytesSpan();
        return span.IsEmpty ? [] : span.ToArray();
    }

    public IReadOnlyList<string> ReadStringList()
    {
        var count = ReadLength();
        if (count == 0)
        {
            return [];
        }

        var values = new string[count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = ReadString();
        }

        return values;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadLength() => CheckedLength(ReadVarUInt64());

    private ulong ReadVarUInt64Slow()
    {
        if (_faulted)
        {
            return 0;
        }

        var position = _position;
        var length = _buffer.Length;
        ulong result = 0;
        for (var index = 0; index < MaxVarIntBytes; index++)
        {
            if ((uint)position >= (uint)length)
            {
                _position = position;
                _faulted = true;
                return 0;
            }

            var current = _buffer[position++];
            if (index == MaxVarIntBytes - 1 && current > 1)
            {
                _position = position;
                _faulted = true;
                return 0;
            }

            result |= (ulong)(current & 0x7F) << (7 * index);
            if ((current & 0x80) == 0)
            {
                _position = position;
                return result;
            }
        }

        _position = position;
        _faulted = true;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CheckedLength(ulong length)
    {
        if (length <= (ulong)(_buffer.Length - _position))
        {
            return (int)length;
        }

        _faulted = true;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> Take(int count)
    {
        var position = _position;
        if (_faulted || (uint)count > (uint)(_buffer.Length - position))
        {
            _faulted = true;
            return [];
        }

        _position = position + count;
        return _buffer.Slice(position, count);
    }
}
