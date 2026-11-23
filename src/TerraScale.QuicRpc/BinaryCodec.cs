using System.Buffers;
using System.Buffers.Binary;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

internal readonly record struct FrameHeader(int PayloadLength, ushort MessageType, byte Control, ulong RequestId)
{
    private const byte KnownControl = QuicProtocol.ControlEndOfStream | QuicProtocol.ControlError;

    public bool IsEndOfStream => (Control & QuicProtocol.ControlEndOfStream) != 0;

    public bool IsError => (Control & QuicProtocol.ControlError) != 0;

    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)PayloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], MessageType);
        destination[6] = Control;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[7..], RequestId);
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out FrameHeader header)
    {
        var length = BinaryPrimitives.ReadUInt32LittleEndian(source);
        var control = source[6];
        if (length > QuicProtocol.MaxPayloadBytes || (control & ~KnownControl) != 0)
        {
            header = default;
            return false;
        }

        header = new FrameHeader(
            (int)length,
            BinaryPrimitives.ReadUInt16LittleEndian(source[4..]),
            control,
            BinaryPrimitives.ReadUInt64LittleEndian(source[7..]));
        return true;
    }
}

internal sealed class PooledFrame(FrameHeader header, byte[] buffer) : IDisposable
{
    private byte[] _buffer = buffer;

    public FrameHeader Header { get; } = header;

    public ReadOnlyMemory<byte> Payload => _buffer.AsMemory(0, Header.PayloadLength);

    public int TotalLength => QuicProtocol.FrameHeaderSize + Header.PayloadLength;

    public void Dispose()
    {
        var rented = Interlocked.Exchange(ref _buffer, []);
        if (rented.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

internal sealed class FrameBuilder : IBufferWriter<byte>, IDisposable
{
    private const int InitialPayloadCapacity = 256;

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(QuicProtocol.FrameHeaderSize + InitialPayloadCapacity);
    private int _written = QuicProtocol.FrameHeaderSize;

    public int PayloadLength => _written - QuicProtocol.FrameHeaderSize;

    public static FrameBuilder Encode<T>(T message)
        where T : IBinaryMessage<T>
    {
        ArgumentNullException.ThrowIfNull(message);
        var builder = new FrameBuilder();
        message.WriteTo(builder);
        return builder;
    }

    public static FrameBuilder EncodeError(QuicRemoteError error)
    {
        var builder = new FrameBuilder();
        builder.WriteString(error.Code);
        builder.WriteString(error.Message);
        return builder;
    }

    public static FrameBuilder Empty() => new();

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _written);
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public ReadOnlyMemory<byte> Seal(ushort messageType, byte control, ulong requestId)
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
        new FrameHeader(PayloadLength, messageType, control, requestId).WriteTo(_buffer);
        return _buffer.AsMemory(0, _written);
    }

    public byte[] Detach(out int length)
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
        var detached = _buffer;
        length = _written;
        _buffer = [];
        _written = 0;
        return detached;
    }

    public void Dispose()
    {
        var rented = _buffer;
        _buffer = [];
        _written = 0;
        if (rented.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void Ensure(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
        var required = Math.Max(sizeHint, 1);
        if (_buffer.Length - _written >= required)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)PayloadLength + required, QuicProtocol.MaxPayloadBytes, nameof(sizeHint));
        var next = ArrayPool<byte>.Shared.Rent(Math.Min(
            Math.Max(_buffer.Length * 2, _written + required),
            QuicProtocol.FrameHeaderSize + QuicProtocol.MaxPayloadBytes));
        _buffer.AsSpan(0, _written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }
}
