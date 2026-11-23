using System.Net.Quic;
using System.Runtime.Versioning;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

internal sealed record StreamOpened;

internal readonly union ServerStreamOpenOutcome(StreamOpened, QuicFault);

internal sealed record ServerStreamItem<T>(QuicCallOutcome<T> Outcome, bool Continue);

internal readonly union ServerStreamReadOutcome<T>(ServerStreamItem<T>, StreamEnded);

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class ServerStreamCall<TRes>(
    QuicRpcClientCore client,
    QuicMeshMetrics metrics,
    TimeProvider timeProvider,
    ushort messageTypeId,
    ulong requestId,
    CancellationToken cancellationToken) : IAsyncDisposable
    where TRes : IBinaryMessage<TRes>
{
    private readonly CallScope _scope = new(QuicMeshLimits.CallTimeout, timeProvider, client.Pool.ShutdownToken, cancellationToken);
    private readonly long _started = timeProvider.GetTimestamp();
    private StreamLease? _lease;
    private bool _entered;
    private bool _finished;
    private bool _completed;
    private QuicOutcome _outcome = QuicOutcome.Ok;

    public async ValueTask<ServerStreamOpenOutcome> OpenAsync(string peerId, FrameBuilder builder)
    {
        client.InFlight.Enter();
        _entered = true;
        var encoded = builder.Seal(messageTypeId, QuicProtocol.ControlEndOfStream, requestId);
        var outcome = await client.OpenAsync(peerId, QuicStreamType.Bidirectional, _scope);
        return outcome switch
        {
            StreamLease lease => await SendAsync(lease, encoded),
            QuicFault fault => FailOpen(fault),
        };
    }

    public async ValueTask<ServerStreamReadOutcome<TRes>> NextAsync()
    {
        if (_finished || _lease is not { } lease)
        {
            return new StreamEnded();
        }

        var outcome = await QuicIo.ReadFrameAsync(lease.Stream, _scope);
        return outcome switch
        {
            PooledFrame frame => Interpret(frame),
            StreamEnded => Finish(new QuicRemoteError(QuicRemoteError.ProtocolViolation, $"The peer ended server stream {requestId} without an end-of-stream frame."), QuicOutcome.RemoteError),
            QuicFault fault => FailRead(lease, fault),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_lease is { } lease)
        {
            _lease = null;
            if (!_completed)
            {
                lease.Stream.Abort(QuicAbortDirection.Both, QuicProtocol.StreamErrorCode);
                _ = QuicIo.DisposeStreamAsync(lease.Stream);
            }
            else
            {
                await QuicIo.DisposeStreamAsync(lease.Stream);
            }

            metrics.StreamClosed(QuicSide.Client);
        }

        _scope.Dispose();
        if (_entered)
        {
            _entered = false;
            var outcome = _finished ? _outcome : QuicOutcome.Canceled;
            metrics.Completed(QuicSide.Client, QuicPattern.ServerStream, messageTypeId, outcome, timeProvider.GetElapsedTime(_started));
            client.InFlight.Exit();
        }
    }

    private async ValueTask<ServerStreamOpenOutcome> SendAsync(StreamLease lease, ReadOnlyMemory<byte> encoded)
    {
        _lease = lease;
        metrics.StreamOpened(QuicSide.Client);
        var outcome = await QuicIo.WriteAsync(lease.Stream, encoded, true, _scope);
        return outcome switch
        {
            QuicSent => Sent(encoded.Length),
            QuicFault fault => FailSend(lease, fault),
        };
    }

    private StreamOpened Sent(int length)
    {
        metrics.Sent(QuicSide.Client, length);
        _scope.DisarmDeadline();
        return new StreamOpened();
    }

    private ServerStreamReadOutcome<TRes> Interpret(PooledFrame frame)
    {
        metrics.Received(QuicSide.Client, frame.TotalLength);
        var header = frame.Header;
        if (header.IsEndOfStream && !header.IsError && header.RequestId == requestId && header.MessageType == messageTypeId)
        {
            frame.Dispose();
            _completed = true;
            _finished = true;
            return new StreamEnded();
        }

        var decoded = QuicRpcClientCore.Decode<TRes>(frame, messageTypeId, requestId);
        var outcome = QuicRpcClientCore.Outcome(decoded);
        if (outcome == QuicOutcome.Ok)
        {
            return new ServerStreamItem<TRes>(decoded, true);
        }

        _completed = header.IsError && header.IsEndOfStream;
        return Finish(decoded, outcome);
    }

    private ServerStreamItem<TRes> Finish(QuicCallOutcome<TRes> item, QuicOutcome outcome)
    {
        _finished = true;
        _outcome = outcome;
        return new ServerStreamItem<TRes>(item, false);
    }

    private QuicFault FailOpen(QuicFault fault)
    {
        metrics.Error(QuicSide.Client, fault);
        _finished = true;
        _outcome = QuicRpcClientCore.Outcome(QuicFaults.ToCallFailure<TRes>(fault));
        return fault;
    }

    private QuicFault FailSend(StreamLease lease, QuicFault fault)
    {
        client.Pool.InvalidateIfDead(lease, fault);
        return FailOpen(fault);
    }

    private ServerStreamItem<TRes> FailRead(StreamLease lease, QuicFault fault)
    {
        client.Pool.InvalidateIfDead(lease, fault);
        metrics.Error(QuicSide.Client, fault);
        var failure = QuicFaults.ToCallFailure<TRes>(fault);
        return Finish(failure, QuicRpcClientCore.Outcome(failure));
    }
}
