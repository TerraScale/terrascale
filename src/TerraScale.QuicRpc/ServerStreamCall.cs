using System.Net.Quic;
using System.Runtime.Versioning;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

internal readonly record struct StreamStep<T>(Result<T, Unavailable, RequestTimeout, QuicRemoteError>? Item, bool Continue);

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

    public async ValueTask<StreamStep<TRes>> OpenAsync(string peerId, FrameBuilder builder)
    {
        client.InFlight.Enter();
        _entered = true;
        var encoded = builder.Seal(messageTypeId, QuicProtocol.ControlEndOfStream, requestId);
        return await client.OpenAsync(peerId, QuicStreamType.Bidirectional, _scope).ConfigureAwait(false) switch
        {
            StreamLease lease => await SendAsync(lease, encoded).ConfigureAwait(false),
            QuicFault fault => Fail(fault),
        };
    }

    public async ValueTask<StreamStep<TRes>> NextAsync()
    {
        if (_finished || _lease is not { } lease)
        {
            return new StreamStep<TRes>(null, false);
        }

        return await QuicIo.ReadFrameAsync(lease.Stream, _scope).ConfigureAwait(false) switch
        {
            PooledFrame frame => Interpret(frame),
            StreamEnded => Finish(new QuicRemoteError(QuicRemoteError.ProtocolViolation, $"The peer ended server stream {requestId} without an end-of-stream frame."), QuicOutcome.RemoteError),
            QuicFault fault => await FailAsync(lease, fault).ConfigureAwait(false),
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
            }

            await lease.Stream.DisposeAsync().ConfigureAwait(false);
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

    private async ValueTask<StreamStep<TRes>> SendAsync(StreamLease lease, ReadOnlyMemory<byte> encoded)
    {
        _lease = lease;
        metrics.StreamOpened(QuicSide.Client);
        return await QuicIo.WriteAsync(lease.Stream, encoded, true, _scope).ConfigureAwait(false) switch
        {
            Done => Sent(encoded.Length),
            QuicFault fault => await FailAsync(lease, fault).ConfigureAwait(false),
        };
    }

    private StreamStep<TRes> Sent(int length)
    {
        metrics.Sent(QuicSide.Client, length);
        _scope.DisarmDeadline();
        return new StreamStep<TRes>(null, true);
    }

    private StreamStep<TRes> Interpret(PooledFrame frame)
    {
        metrics.Received(QuicSide.Client, frame.TotalLength);
        var header = frame.Header;
        if (header.IsEndOfStream && !header.IsError && header.RequestId == requestId && header.MessageType == messageTypeId)
        {
            frame.Dispose();
            _completed = true;
            _finished = true;
            return new StreamStep<TRes>(null, false);
        }

        var decoded = QuicRpcClientCore.Decode<TRes>(frame, messageTypeId, requestId);
        var outcome = QuicRpcClientCore.Outcome(decoded);
        if (outcome == QuicOutcome.Ok)
        {
            return new StreamStep<TRes>(decoded, true);
        }

        _completed = header.IsError && header.IsEndOfStream;
        return Finish(decoded, outcome);
    }

    private StreamStep<TRes> Finish(Result<TRes, Unavailable, RequestTimeout, QuicRemoteError> item, QuicOutcome outcome)
    {
        _finished = true;
        _outcome = outcome;
        return new StreamStep<TRes>(item, false);
    }

    private StreamStep<TRes> Fail(QuicFault fault)
    {
        metrics.Error(QuicSide.Client, fault);
        var failure = QuicFaults.ToCallFailure<TRes>(fault);
        return Finish(failure, QuicRpcClientCore.Outcome(failure));
    }

    private async ValueTask<StreamStep<TRes>> FailAsync(StreamLease lease, QuicFault fault)
    {
        await client.Pool.InvalidateIfDeadAsync(lease, fault).ConfigureAwait(false);
        return Fail(fault);
    }
}
