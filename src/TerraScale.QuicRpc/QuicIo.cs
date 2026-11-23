using System.Buffers;
using System.Diagnostics;
using System.Net.Quic;
using System.Net.Sockets;
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

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal static class QuicIo
{
    private static readonly StreamEnded Ended = new();

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

    public static async ValueTask<QuicIoWriteOutcome> WriteAsync(QuicStream stream, ReadOnlyMemory<byte> frame, bool completeWrites, CallScope scope)
    {
        try
        {
            await stream.WriteAsync(frame, completeWrites, scope.Token);
            return new QuicSent();
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

    public static async ValueTask<FrameRead> ReadFrameAsync(QuicStream stream, CallScope scope)
    {
        var header = ArrayPool<byte>.Shared.Rent(QuicProtocol.FrameHeaderSize);
        byte[]? payload = null;
        try
        {
            var read = await stream.ReadAtLeastAsync(header.AsMemory(0, QuicProtocol.FrameHeaderSize), QuicProtocol.FrameHeaderSize, throwOnEndOfStream: false, scope.Token);
            if (read == 0)
            {
                return Ended;
            }

            if (read < QuicProtocol.FrameHeaderSize || !FrameHeader.TryRead(header, out var parsed))
            {
                return new ProtocolFault("The peer sent a truncated or invalid frame header.");
            }

            if (parsed.PayloadLength == 0)
            {
                return new PooledFrame(parsed, []);
            }

            payload = ArrayPool<byte>.Shared.Rent(parsed.PayloadLength);
            await stream.ReadExactlyAsync(payload.AsMemory(0, parsed.PayloadLength), scope.Token);
            var frame = new PooledFrame(parsed, payload);
            payload = null;
            return frame;
        }
        catch (QuicException exception)
        {
            return Classify(exception);
        }
        catch (AuthenticationException exception)
        {
            return new PeerRejected(exception.Message);
        }
        catch (EndOfStreamException)
        {
            return new ProtocolFault("The peer ended the stream inside a frame payload.");
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
            ArrayPool<byte>.Shared.Return(header);
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

    public static async Task DisposeStreamAsync(QuicStream stream)
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

    public static async Task<bool> PeerStoppedReadingAsync(QuicStream stream, Task finished)
    {
        var writesClosed = stream.WritesClosed;
        await Task.WhenAny(writesClosed, finished);
        return writesClosed.IsCanceled || writesClosed.Exception is not null;
    }

    private static QuicFault Classify(QuicException exception) => exception.QuicError switch
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
