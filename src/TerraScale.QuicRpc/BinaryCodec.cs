using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

internal readonly record struct FrameHeader(int PayloadLength, ushort MessageType, byte Control, ulong RequestId)
{
    private const byte KnownControl = QuicProtocol.ControlEndOfStream | QuicProtocol.ControlError;

    public bool IsEndOfStream => (Control & QuicProtocol.ControlEndOfStream) != 0;

    public bool IsError => (Control & QuicProtocol.ControlError) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)PayloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], MessageType);
        destination[6] = Control;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[7..], RequestId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

internal sealed class PooledFrame : IDisposable
{
    private const int MaxPoolSize = 64;

    private static readonly ConcurrentQueue<PooledFrame> Pool = new();
    private static int PoolCount;

    [ThreadStatic]
    private static PooledFrame? ThreadLocal;

    private byte[] _buffer = [];
    private int _offset;
    private int _disposed;

    public FrameHeader Header { get; private set; }

    public ReadOnlyMemory<byte> Payload => _buffer.AsMemory(_offset, Header.PayloadLength);

    public int TotalLength => QuicProtocol.FrameHeaderSize + Header.PayloadLength;

    public static PooledFrame Rent(FrameHeader header, byte[] buffer, int offset = 0)
    {
        var frame = ThreadLocal;
        if (frame is not null)
        {
            ThreadLocal = null;
        }
        else if (Pool.TryDequeue(out frame))
        {
            Interlocked.Decrement(ref PoolCount);
        }
        else
        {
            frame = new PooledFrame();
        }

        frame.Header = header;
        frame._buffer = buffer;
        frame._offset = offset;
        frame._disposed = 0;
        return frame;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var rented = _buffer;
        _buffer = [];
        _offset = 0;
        Header = default;
        if (rented.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        if (ThreadLocal is null)
        {
            ThreadLocal = this;
        }
        else if (Interlocked.Increment(ref PoolCount) <= MaxPoolSize)
        {
            Pool.Enqueue(this);
        }
        else
        {
            Interlocked.Decrement(ref PoolCount);
        }
    }
}

internal sealed class FrameBuilder : IBufferWriter<byte>, IDisposable
{
    private const int InitialBufferCapacity = 4 * 1024;
    private const int MaxPooledBufferSize = 256 * 1024;
    private const int MaxPoolSize = 64;

    private static readonly ConcurrentQueue<FrameBuilder> Pool = new();
    private static int PoolCount;

    [ThreadStatic]
    private static FrameBuilder? ThreadLocal;

    private byte[] _buffer = [];
    private int _written;
    private bool _disposed;

    private FrameBuilder()
    {
    }

    public int PayloadLength => _written - QuicProtocol.FrameHeaderSize;

    public static FrameBuilder Rent()
    {
        var builder = ThreadLocal;
        if (builder is not null)
        {
            ThreadLocal = null;
        }
        else if (Pool.TryDequeue(out builder))
        {
            Interlocked.Decrement(ref PoolCount);
        }
        else
        {
            builder = new FrameBuilder();
        }

        if (builder._buffer.Length == 0)
        {
            builder._buffer = ArrayPool<byte>.Shared.Rent(InitialBufferCapacity);
        }

        builder._written = QuicProtocol.FrameHeaderSize;
        builder._disposed = false;
        return builder;
    }

    public static FrameBuilder Encode<T>(T message)
        where T : IBinaryMessage<T>
    {
        ArgumentNullException.ThrowIfNull(message);
        var builder = Rent();
        message.WriteTo(builder);
        return builder;
    }

    public static FrameBuilder EncodeError(QuicRemoteError error)
    {
        var builder = Rent();
        builder.WriteString(error.Code);
        builder.WriteString(error.Message);
        return builder;
    }

    public static FrameBuilder Empty() => Rent();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _written);
        _written += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlyMemory<byte> Seal(ushort messageType, byte control, ulong requestId)
    {
        ObjectDisposedException.ThrowIf(_disposed || _buffer.Length == 0, this);
        new FrameHeader(PayloadLength, messageType, control, requestId).WriteTo(_buffer);
        return _buffer.AsMemory(0, _written);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _written = 0;

        if (_buffer.Length > MaxPooledBufferSize)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }

        if (ThreadLocal is null)
        {
            ThreadLocal = this;
        }
        else if (Interlocked.Increment(ref PoolCount) <= MaxPoolSize)
        {
            Pool.Enqueue(this);
        }
        else
        {
            Interlocked.Decrement(ref PoolCount);
            if (_buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = [];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed || _buffer.Length == 0, this);
        var required = sizeHint > 0 ? sizeHint : 1;
        if (_buffer.Length - _written >= required)
        {
            return;
        }

        Grow(required);
    }

    private void Grow(int required)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)PayloadLength + required, QuicProtocol.MaxPayloadBytes, nameof(required));
        var next = ArrayPool<byte>.Shared.Rent(Math.Min(
            Math.Max(_buffer.Length * 2, _written + required),
            QuicProtocol.FrameHeaderSize + QuicProtocol.MaxPayloadBytes));
        _buffer.AsSpan(0, _written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }
}
