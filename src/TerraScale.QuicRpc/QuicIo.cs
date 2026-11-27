using System.Buffers;
using System.Diagnostics;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Authentication;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

internal readonly union QuicListenerOpenOutcome(QuicListener, QuicFault);

internal readonly union QuicConnectionOpenOutcome(QuicConnection, QuicFault);

internal readonly union QuicConnectionAcceptOutcome(QuicConnection, QuicFault);

internal readonly union QuicStreamOpenOutcome(QuicStream, QuicFault);

internal readonly union QuicStreamAcceptOutcome(QuicStream, QuicFault);

internal readonly union QuicIoWriteOutcome(QuicSent, QuicFault);

internal sealed record QuicConnectionClosed;

internal readonly union QuicConnectionCloseOutcome(QuicConnectionClosed, QuicFault);

internal sealed record FrameReady;

internal readonly union StreamFrameStatus(FrameReady, StreamEnded, QuicFault);

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class QuicFrameReader(int initialBufferCapacity = QuicFrameReader.DefaultBufferCapacity) : IDisposable
{
    public const int DefaultBufferCapacity = 32 * 1024;
    public const int UnaryBufferCapacity = 4 * 1024;
    private const int DirectRentThreshold = 256 * 1024;
    private static readonly FrameReady Ready = new();
    private static readonly StreamEnded Ended = new();

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(initialBufferCapacity);
    private byte[]? _largePayload;
    private int _offset;
    private int _count;

    public FrameHeader CurrentHeader { get; private set; }

    public ReadOnlyMemory<byte> CurrentPayload { get; private set; }

    public int CurrentTotalLength => QuicProtocol.FrameHeaderSize + CurrentHeader.PayloadLength;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<StreamFrameStatus>))]
    public async ValueTask<StreamFrameStatus> ReadNextAsync(QuicStream stream, CallScope scope)
    {
        ReleaseLargePayload();
        CurrentPayload = ReadOnlyMemory<byte>.Empty;

        try
        {
            if (_count == 0)
            {
                _offset = 0;
            }

            while (_count < QuicProtocol.FrameHeaderSize)
            {
                if (_buffer.Length - (_offset + _count) < QuicProtocol.FrameHeaderSize - _count)
                {
                    if (_count > 0)
                    {
                        _buffer.AsSpan(_offset, _count).CopyTo(_buffer);
                    }

                    _offset = 0;
                }

                var read = await stream.ReadAsync(_buffer.AsMemory(_offset + _count), scope.Token);
                if (read == 0)
                {
                    return _count == 0
                        ? Ended
                        : new ProtocolFault("The peer sent a truncated or invalid frame header.");
                }

                _count += read;
            }

            if (!FrameHeader.TryRead(_buffer.AsSpan(_offset, QuicProtocol.FrameHeaderSize), out var parsed))
            {
                return new ProtocolFault("The peer sent a truncated or invalid frame header.");
            }

            _offset += QuicProtocol.FrameHeaderSize;
            _count -= QuicProtocol.FrameHeaderSize;

            var payloadLength = parsed.PayloadLength;
            if (payloadLength == 0)
            {
                if (_count == 0)
                {
                    _offset = 0;
                }

                CurrentHeader = parsed;
                CurrentPayload = ReadOnlyMemory<byte>.Empty;
                return Ready;
            }

            if (payloadLength <= _buffer.Length)
            {
                if (_count < payloadLength)
                {
                    if (_buffer.Length - _offset < payloadLength)
                    {
                        if (_count > 0)
                        {
                            _buffer.AsSpan(_offset, _count).CopyTo(_buffer);
                        }

                        _offset = 0;
                    }

                    while (_count < payloadLength)
                    {
                        var read = await stream.ReadAsync(_buffer.AsMemory(_offset + _count), scope.Token);
                        if (read == 0)
                        {
                            return new ProtocolFault("The peer ended the stream inside a frame payload.");
                        }

                        _count += read;
                    }
                }

                var payloadStart = _offset;
                _offset += payloadLength;
                _count -= payloadLength;
                if (_count == 0)
                {
                    _offset = 0;
                }

                CurrentHeader = parsed;
                CurrentPayload = _buffer.AsMemory(payloadStart, payloadLength);
                return Ready;
            }

            var initialSize = Math.Min(payloadLength, DirectRentThreshold);
            var payload = ArrayPool<byte>.Shared.Rent(initialSize);
            try
            {
                var received = Math.Min(_count, payloadLength);
                if (received > 0)
                {
                    _buffer.AsSpan(_offset, received).CopyTo(payload);
                    _offset += received;
                    _count -= received;
                    if (_count == 0)
                    {
                        _offset = 0;
                    }
                }

                while (received < payloadLength)
                {
                    var target = Math.Min(payload.Length, payloadLength);
                    var needed = target - received;
                    var count = await stream.ReadAtLeastAsync(payload.AsMemory(received, needed), needed, throwOnEndOfStream: false, scope.Token);
                    received += count;
                    if (received < target)
                    {
                        return new ProtocolFault("The peer ended the stream inside a frame payload.");
                    }

                    if (received < payloadLength)
                    {
                        var next = ArrayPool<byte>.Shared.Rent(Math.Min(target * 2, payloadLength));
                        payload.AsSpan(0, received).CopyTo(next);
                        ArrayPool<byte>.Shared.Return(payload);
                        payload = next;
                    }
                }

                _largePayload = payload;
                payload = null;
                CurrentHeader = parsed;
                CurrentPayload = _largePayload.AsMemory(0, payloadLength);
                return Ready;
            }
            finally
            {
                if (payload is not null)
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }
            }
        }
        catch (QuicException exception)
        {
            return QuicIo.Classify(exception);
        }
        catch (AuthenticationException exception)
        {
            return new PeerRejected(exception.Message);
        }
        catch (SocketException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (ObjectDisposedException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (IOException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (OperationCanceledException) when (!scope.Caller.IsCancellationRequested)
        {
            return scope.Interrupted();
        }
    }

    public void Dispose()
    {
        ReleaseLargePayload();
        var buffer = _buffer;
        _buffer = [];
        _offset = 0;
        _count = 0;
        CurrentPayload = ReadOnlyMemory<byte>.Empty;
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ReleaseLargePayload()
    {
        if (_largePayload is { } large)
        {
            _largePayload = null;
            ArrayPool<byte>.Shared.Return(large);
        }
    }
}

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal static class QuicIo
{
    private const int SingleReadInitialBufferSize = 4 * 1024;
    private const int DirectRentThreshold = 256 * 1024;
    private static readonly StreamEnded Ended = new();
    private static readonly QuicSent Sent = new();

    public static async ValueTask<QuicListenerOpenOutcome> ListenAsync(QuicListenerOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return await QuicListener.ListenAsync(options, cancellationToken);
        }
        catch (QuicException exception)
        {
            return Classify(exception);
        }
        catch (SocketException exception)
        {
            return new ConnectionLost(exception.Message);
        }
    }

    public static async ValueTask<QuicConnectionOpenOutcome> ConnectAsync(QuicClientConnectionOptions options, CallScope scope)
    {
        try
        {
            return await QuicConnection.ConnectAsync(options, scope.Token);
        }
        catch (QuicException exception)
        {
            return Classify(exception);
        }
        catch (SocketException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (AuthenticationException exception)
        {
            return new PeerRejected(exception.Message);
        }
        catch (OperationCanceledException) when (!scope.Caller.IsCancellationRequested)
        {
            return scope.Interrupted();
        }
    }

    public static async ValueTask<QuicConnectionAcceptOutcome> AcceptConnectionAsync(QuicListener listener, CallScope scope)
    {
        try
        {
            return await listener.AcceptConnectionAsync(scope.Token);
        }
        catch (QuicException exception)
        {
            return Classify(exception);
        }
        catch (SocketException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (AuthenticationException exception)
        {
            return new PeerRejected(exception.Message);
        }
        catch (ObjectDisposedException exception)
        {
            return new TransportShutdown(exception.Message);
        }
        catch (OperationCanceledException) when (!scope.Caller.IsCancellationRequested)
        {
            return scope.Interrupted();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<QuicStreamAcceptOutcome>))]
    public static async ValueTask<QuicStreamAcceptOutcome> AcceptStreamAsync(QuicConnection connection, CallScope scope)
    {
        try
        {
            return await connection.AcceptInboundStreamAsync(scope.Token);
        }
        catch (QuicException exception)
        {
            return Classify(exception);
        }
        catch (AuthenticationException exception)
        {
            return new PeerRejected(exception.Message);
        }
        catch (ObjectDisposedException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (OperationCanceledException) when (!scope.Caller.IsCancellationRequested)
        {
            return scope.Interrupted();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<QuicStreamOpenOutcome>))]
    public static async ValueTask<QuicStreamOpenOutcome> OpenStreamAsync(QuicConnection connection, QuicStreamType type, CallScope scope)
    {
        try
        {
            return await connection.OpenOutboundStreamAsync(type, scope.Token);
        }
        catch (QuicException exception)
        {
            return Classify(exception);
        }
        catch (AuthenticationException exception)
        {
            return new PeerRejected(exception.Message);
        }
        catch (SocketException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (ObjectDisposedException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (OperationCanceledException) when (!scope.Caller.IsCancellationRequested)
        {
            return scope.Interrupted();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<QuicIoWriteOutcome>))]
    public static async ValueTask<QuicIoWriteOutcome> WriteAsync(QuicStream stream, ReadOnlyMemory<byte> frame, bool completeWrites, CallScope scope)
    {
        try
        {
            await stream.WriteAsync(frame, completeWrites, scope.Token);
            return Sent;
        }
        catch (QuicException exception)
        {
            return Classify(exception);
        }
        catch (AuthenticationException exception)
        {
            return new PeerRejected(exception.Message);
        }
        catch (SocketException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (ObjectDisposedException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (IOException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (OperationCanceledException) when (!scope.Caller.IsCancellationRequested)
        {
            return scope.Interrupted();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<FrameRead>))]
    public static async ValueTask<FrameRead> ReadSingleFrameAsync(QuicStream stream, CallScope scope)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(SingleReadInitialBufferSize);
        byte[]? payload = null;
        try
        {
            var totalRead = await stream.ReadAsync(buffer, scope.Token);
            if (totalRead == 0)
            {
                return Ended;
            }

            while (totalRead < QuicProtocol.FrameHeaderSize)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(totalRead), scope.Token);
                if (n == 0)
                {
                    return new ProtocolFault("The peer sent a truncated or invalid frame header.");
                }

                totalRead += n;
            }

            if (!FrameHeader.TryRead(buffer, out var parsed))
            {
                return new ProtocolFault("The peer sent a truncated or invalid frame header.");
            }

            var payloadLength = parsed.PayloadLength;
            if (payloadLength == 0)
            {
                return PooledFrame.Rent(parsed, []);
            }

            var frameLength = QuicProtocol.FrameHeaderSize + payloadLength;
            if (frameLength <= buffer.Length)
            {
                if (totalRead < frameLength)
                {
                    var remaining = frameLength - totalRead;
                    var extra = await stream.ReadAtLeastAsync(buffer.AsMemory(totalRead, remaining), remaining, throwOnEndOfStream: false, scope.Token);
                    if (extra < remaining)
                    {
                        return new ProtocolFault("The peer ended the stream inside a frame payload.");
                    }
                }

                var frame = PooledFrame.Rent(parsed, buffer, QuicProtocol.FrameHeaderSize);
                buffer = null;
                return frame;
            }

            var initialCapacity = Math.Min(payloadLength, DirectRentThreshold);
            payload = ArrayPool<byte>.Shared.Rent(initialCapacity);
            var received = Math.Min(totalRead - QuicProtocol.FrameHeaderSize, payloadLength);
            if (received > 0)
            {
                buffer.AsSpan(QuicProtocol.FrameHeaderSize, received).CopyTo(payload);
            }

            ArrayPool<byte>.Shared.Return(buffer);
            buffer = null;

            while (received < payloadLength)
            {
                var capacity = Math.Min(payload.Length, payloadLength);
                var needed = capacity - received;
                var count = await stream.ReadAtLeastAsync(payload.AsMemory(received, needed), needed, throwOnEndOfStream: false, scope.Token);
                received += count;
                if (received < capacity)
                {
                    return new ProtocolFault("The peer ended the stream inside a frame payload.");
                }

                if (received < payloadLength)
                {
                    var next = ArrayPool<byte>.Shared.Rent(Math.Min(capacity * 2, payloadLength));
                    payload.AsSpan(0, received).CopyTo(next);
                    ArrayPool<byte>.Shared.Return(payload);
                    payload = next;
                }
            }

            var largeFrame = PooledFrame.Rent(parsed, payload);
            payload = null;
            return largeFrame;
        }
        catch (QuicException exception)
        {
            return Classify(exception);
        }
        catch (AuthenticationException exception)
        {
            return new PeerRejected(exception.Message);
        }
        catch (SocketException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (ObjectDisposedException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (IOException exception)
        {
            return new ConnectionLost(exception.Message);
        }
        catch (OperationCanceledException) when (!scope.Caller.IsCancellationRequested)
        {
            return scope.Interrupted();
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (payload is not null)
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    public static async ValueTask<QuicConnectionCloseOutcome> CloseAsync(QuicConnection connection, long errorCode)
    {
        try
        {
            await connection.CloseAsync(errorCode, CancellationToken.None);
            return new QuicConnectionClosed();
        }
        catch (QuicException exception)
        {
            return Classify(exception);
        }
        catch (AuthenticationException exception)
        {
            return new PeerRejected(exception.Message);
        }
        catch (ObjectDisposedException exception)
        {
            return new ConnectionLost(exception.Message);
        }
    }

    public static async Task DisposeConnectionAsync(QuicConnection connection)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch (QuicException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public static async ValueTask DisposeStreamAsync(QuicStream stream)
    {
        try
        {
            await stream.DisposeAsync();
        }
        catch (QuicException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public static void DisposeStreamFireAndForget(QuicStream stream) => _ = DisposeStreamTaskAsync(stream);

    private static async Task DisposeStreamTaskAsync(QuicStream stream) => await DisposeStreamAsync(stream);

    internal static QuicFault Classify(QuicException exception) => exception.QuicError switch
    {
        QuicError.StreamAborted => new StreamReset(exception.ApplicationErrorCode ?? 0, exception.Message),
        QuicError.OperationAborted => new LocalAbort(exception.Message),
        QuicError.CallbackError => new PeerRejected(exception.Message),
        QuicError.Success => new ConnectionLost(exception.Message),
        QuicError.InternalError => new ConnectionLost(exception.Message),
        QuicError.ConnectionAborted => new ConnectionLost(exception.Message),
        QuicError.ConnectionTimeout => new ConnectionLost(exception.Message),
        QuicError.ConnectionRefused => new ConnectionLost(exception.Message),
        QuicError.VersionNegotiationError => new ConnectionLost(exception.Message),
        QuicError.ConnectionIdle => new ConnectionLost(exception.Message),
        QuicError.AlpnInUse => new ConnectionLost(exception.Message),
        QuicError.TransportError => new ConnectionLost(exception.Message),
        _ => throw new UnreachableException(),
    };
}
