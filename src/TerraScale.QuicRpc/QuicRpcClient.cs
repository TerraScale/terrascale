using System.Net.Quic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

internal readonly union PeerSlotLookup(PeerSlot, QuicFault);

public sealed class QuicRpcClient : IAsyncDisposable
{
    private readonly QuicRpcClientCore _core;

    public QuicRpcClient(QuicMeshOptions options, QuicMeshMetrics metrics, TimeProvider timeProvider, ILogger<QuicRpcClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        var validated = options.Validate(timeProvider);
        if (!QuicPlatform.IsSupported)
        {
            throw new PlatformNotSupportedException("System.Net.Quic is unavailable: libmsquic.so.2 (MsQuic 2.x) could not be loaded from the application directory, runtimes/<rid>/native or the system library path.");
        }

        NodeId = validated.NodeId;
        Region = validated.Region;
        _core = new QuicRpcClientCore(validated, metrics, timeProvider, logger);
    }

    public string NodeId { get; }

    public string Region { get; }

    public void UpsertPeer(QuicPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (QuicPlatform.IsSupported)
        {
            _core.UpsertPeer(peer.Validate());
        }
    }

    public ValueTask RemovePeerAsync(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        return QuicPlatform.IsSupported ? _core.RemovePeerAsync(nodeId) : ValueTask.CompletedTask;
    }

    public void ResetPeer(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (QuicPlatform.IsSupported)
        {
            _core.ResetPeer(nodeId);
        }
    }

    public ValueTask<QuicCallOutcome<TRes>> CallAsync<TReq, TRes>(
        string peerId,
        ushort messageTypeId,
        TReq request,
        CancellationToken cancellationToken)
        where TReq : IBinaryMessage<TReq>
        where TRes : IBinaryMessage<TRes> =>
        QuicPlatform.IsSupported
            ? _core.CallAsync<TReq, TRes>(peerId, messageTypeId, request, null, cancellationToken)
            : new(Unsupported);

    public ValueTask<QuicCallOutcome<TRes>> CallAsync<TReq, TRes>(
        string peerId,
        ushort messageTypeId,
        TReq request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TReq : IBinaryMessage<TReq>
        where TRes : IBinaryMessage<TRes> =>
        QuicPlatform.IsSupported
            ? _core.CallAsync<TReq, TRes>(peerId, messageTypeId, request, timeout, cancellationToken)
            : new(Unsupported);

    public ValueTask<QuicPushOutcome> PushAsync<TMsg>(
        string peerId,
        ushort messageTypeId,
        TMsg message,
        string? orderedChannelKey,
        CancellationToken cancellationToken)
        where TMsg : IBinaryMessage<TMsg> =>
        QuicPlatform.IsSupported
            ? _core.PushAsync(peerId, messageTypeId, message, orderedChannelKey, cancellationToken)
            : new(Unsupported);

    public IAsyncEnumerable<QuicCallOutcome<TRes>> ServerStreamAsync<TReq, TRes>(
        string peerId,
        ushort messageTypeId,
        TReq request,
        CancellationToken cancellationToken)
        where TReq : IBinaryMessage<TReq>
        where TRes : IBinaryMessage<TRes> =>
        QuicPlatform.IsSupported
            ? _core.ServerStreamAsync<TReq, TRes>(peerId, messageTypeId, request, cancellationToken)
            : UnsupportedStream<TRes>();

    public ValueTask DisposeAsync() => QuicPlatform.IsSupported ? _core.DisposeAsync() : ValueTask.CompletedTask;

    private static QuicUnavailable Unsupported { get; } = new("System.Net.Quic is not supported on this platform.", "quic_unsupported");

    private static async IAsyncEnumerable<QuicCallOutcome<TRes>> UnsupportedStream<TRes>()
    {
        await Task.CompletedTask;
        yield return Unsupported;
    }
}

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class QuicRpcClientCore : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly QuicConnectionPool _pool;
    private readonly InFlightTracker _inFlight = new();
    private readonly QuicMeshOptions _options;
    private readonly QuicMeshMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private long _nextRequestId;
    private int _disposed;

    public QuicRpcClientCore(QuicMeshOptions options, QuicMeshMetrics metrics, TimeProvider timeProvider, ILogger logger)
    {
        _options = options;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
        _pool = new QuicConnectionPool(options, new QuicCertificateValidator(options.TrustedAuthorities, timeProvider), metrics, timeProvider, logger, _shutdown.Token);
    }

    public QuicConnectionPool Pool => _pool;

    public InFlightTracker InFlight => _inFlight;

    public void UpsertPeer(QuicPeer peer) => _pool.Upsert(peer);

    public ValueTask RemovePeerAsync(string nodeId) => _pool.RemoveAsync(nodeId);

    public void ResetPeer(string nodeId) => _pool.Reset(nodeId);

    public async ValueTask<QuicCallOutcome<TRes>> CallAsync<TReq, TRes>(
        string peerId,
        ushort messageTypeId,
        TReq request,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
        where TReq : IBinaryMessage<TReq>
        where TRes : IBinaryMessage<TRes>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(request);
        var started = _timeProvider.GetTimestamp();
        _inFlight.Enter();
        try
        {
            using var scope = new CallScope(timeout ?? QuicMeshLimits.CallTimeout, _timeProvider, _pool.ShutdownToken, cancellationToken);
            using var builder = FrameBuilder.Encode(request);
            var requestId = NextRequestId();
            var encoded = builder.Seal(messageTypeId, QuicProtocol.ControlEndOfStream, requestId);
            var outcome = await OpenAsync(peerId, QuicStreamType.Bidirectional, scope);
            var result = outcome switch
            {
                StreamLease lease => await ExchangeAsync<TRes>(lease, encoded, messageTypeId, requestId, scope),
                QuicFault fault => Failed<TRes>(fault),
            };
            _metrics.Completed(QuicSide.Client, QuicPattern.Unary, messageTypeId, Outcome(result), _timeProvider.GetElapsedTime(started));
            return result;
        }
        finally
        {
            _inFlight.Exit();
        }
    }

    public async ValueTask<QuicPushOutcome> PushAsync<TMsg>(
        string peerId,
        ushort messageTypeId,
        TMsg message,
        string? orderedChannelKey,
        CancellationToken cancellationToken)
        where TMsg : IBinaryMessage<TMsg>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(message);
        if (orderedChannelKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(orderedChannelKey);
        }

        var started = _timeProvider.GetTimestamp();
        _inFlight.Enter();
        try
        {
            using var scope = new CallScope(QuicMeshLimits.CallTimeout, _timeProvider, _pool.ShutdownToken, cancellationToken);
            using var builder = FrameBuilder.Encode(message);
            var encoded = builder.Seal(messageTypeId, QuicProtocol.ControlNone, NextRequestId());
            var delivered = orderedChannelKey is null
                ? await PushUnorderedAsync(peerId, encoded, scope)
                : await PushOrderedAsync(peerId, orderedChannelKey, builder, scope);
            var result = delivered switch
            {
                QuicSent done => done,
                QuicFault fault => FailedPush(fault),
            };
            var pattern = orderedChannelKey is null ? QuicPattern.Push : QuicPattern.OrderedPush;
            _metrics.Completed(QuicSide.Client, pattern, messageTypeId, Outcome(result), _timeProvider.GetElapsedTime(started));
            return result;
        }
        finally
        {
            _inFlight.Exit();
        }
    }

    public async IAsyncEnumerable<QuicCallOutcome<TRes>> ServerStreamAsync<TReq, TRes>(
        string peerId,
        ushort messageTypeId,
        TReq request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TReq : IBinaryMessage<TReq>
        where TRes : IBinaryMessage<TRes>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(request);
        await using var call = new ServerStreamCall<TRes>(this, _metrics, _timeProvider, messageTypeId, NextRequestId(), cancellationToken);
        using (var builder = FrameBuilder.Encode(request))
        {
            var opened = await call.OpenAsync(peerId, builder);
            var failure = opened switch
            {
                StreamOpened => null,
                QuicFault fault => fault,
            };
            if (failure is not null)
            {
                yield return QuicFaults.ToCallFailure<TRes>(failure);
                yield break;
            }
        }

        while (true)
        {
            var step = await call.NextAsync();
            var item = step switch
            {
                ServerStreamItem<TRes> received => received,
                StreamEnded => null,
            };
            if (item is null)
            {
                yield break;
            }

            yield return item.Outcome;
            if (!item.Continue)
            {
                yield break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var drained = Task.WhenAll(_inFlight.DrainAsync(), _pool.CompleteChannelsAsync());
        if (!await InFlightTracker.WaitAsync(drained, QuicMeshLimits.DrainTimeout, _timeProvider))
        {
            QuicRpcLog.DrainTimedOut(_logger, QuicMeshLimits.DrainTimeout);
        }

        await _shutdown.CancelAsync();
        await _pool.ShutdownAsync();
        _shutdown.Dispose();
    }

    public PeerSlotLookup Resolve(string peerId) =>
        Volatile.Read(ref _disposed) != 0 ? new TransportShutdown("The QUIC client is disposed.")
        : _pool.TryGet(peerId, out var slot) ? slot
        : new PeerUnknown($"Peer '{peerId}' is not registered with the QUIC client.");

    public async ValueTask<StreamLeaseOutcome> OpenAsync(string peerId, QuicStreamType type, CallScope scope) =>
        Resolve(peerId) switch
        {
            PeerSlot slot => await _pool.OpenStreamAsync(slot, type, scope),
            QuicFault fault => fault,
        };

    public static QuicOutcome Outcome<T>(QuicCallOutcome<T> result)
        where T : IBinaryMessage<T> => result switch
        {
            T => QuicOutcome.Ok,
            QuicUnavailable => QuicOutcome.Unavailable,
            QuicTimeout => QuicOutcome.Timeout,
            QuicRemoteError => QuicOutcome.RemoteError,
        };

    public static QuicCallOutcome<T> Decode<T>(PooledFrame frame, ushort messageTypeId, ulong requestId)
        where T : IBinaryMessage<T>
    {
        using (frame)
        {
            var header = frame.Header;
            if (header.RequestId != requestId || header.MessageType != messageTypeId)
            {
                return new QuicRemoteError(QuicRemoteError.ProtocolViolation, $"Response frame for message type {header.MessageType} request {header.RequestId} does not match message type {messageTypeId} request {requestId}.");
            }

            if (header.IsError)
            {
                return ReadRemoteError(frame.Payload.Span);
            }

            return BinaryMessage.TryDecode<T>(frame.Payload.Span, out var response)
                ? response
                : new QuicRemoteError(QuicRemoteError.ProtocolViolation, $"Response payload for message type {messageTypeId} is malformed.");
        }
    }

    private static QuicRemoteError ReadRemoteError(ReadOnlySpan<byte> payload)
    {
        var reader = new WireReader(payload);
        var code = reader.ReadString();
        var message = reader.ReadString();
        return reader.IsComplete
            ? new QuicRemoteError(code, message)
            : new QuicRemoteError(QuicRemoteError.ProtocolViolation, "The peer sent a malformed error frame.");
    }

    private static QuicOutcome Outcome(QuicPushOutcome result) => result switch
    {
        QuicSent => QuicOutcome.Ok,
        QuicUnavailable => QuicOutcome.Unavailable,
        QuicTimeout => QuicOutcome.Timeout,
    };

    private async ValueTask<QuicCallOutcome<TRes>> ExchangeAsync<TRes>(
        StreamLease lease,
        ReadOnlyMemory<byte> encoded,
        ushort messageTypeId,
        ulong requestId,
        CallScope scope)
        where TRes : IBinaryMessage<TRes>
    {
        _metrics.StreamOpened(QuicSide.Client);
        var aborted = false;
        try
        {
            var read = await SendAndReceiveAsync(lease.Stream, encoded, scope);
            var result = read switch
            {
                PooledFrame frame => Decode<TRes>(Received(frame), messageTypeId, requestId),
                StreamEnded => new QuicRemoteError(QuicRemoteError.ProtocolViolation, $"The peer closed request {requestId} without a response frame."),
                QuicFault fault => FailedExchange<TRes>(lease, fault, out aborted),
            };
            return result;
        }
        finally
        {
            if (aborted)
            {
                _ = QuicIo.DisposeStreamAsync(lease.Stream);
            }
            else
            {
                await QuicIo.DisposeStreamAsync(lease.Stream);
            }

            _metrics.StreamClosed(QuicSide.Client);
        }
    }

    private PooledFrame Received(PooledFrame frame)
    {
        _metrics.Received(QuicSide.Client, frame.TotalLength);
        return frame;
    }

    private async ValueTask<FrameRead> SendAndReceiveAsync(QuicStream stream, ReadOnlyMemory<byte> encoded, CallScope scope)
    {
        var written = await QuicIo.WriteAsync(stream, encoded, true, scope);
        return written switch
        {
            QuicSent => await SentThenReadAsync(stream, encoded.Length, scope),
            QuicFault fault => fault,
        };
    }

    private async ValueTask<FrameRead> SentThenReadAsync(QuicStream stream, int sent, CallScope scope)
    {
        _metrics.Sent(QuicSide.Client, sent);
        return await QuicIo.ReadFrameAsync(stream, scope);
    }

    private QuicCallOutcome<TRes> FailedExchange<TRes>(StreamLease lease, QuicFault fault, out bool aborted)
    {
        aborted = true;
        lease.Stream.Abort(QuicAbortDirection.Both, QuicProtocol.StreamErrorCode);
        _pool.InvalidateIfDead(lease, fault);
        return Failed<TRes>(fault);
    }

    private QuicCallOutcome<TRes> Failed<TRes>(QuicFault fault)
    {
        _metrics.Error(QuicSide.Client, fault);
        return QuicFaults.ToCallFailure<TRes>(fault);
    }

    private QuicPushOutcome FailedPush(QuicFault fault)
    {
        _metrics.Error(QuicSide.Client, fault);
        return QuicFaults.ToPushFailure(fault);
    }

    private async ValueTask<PushDeliveryOutcome> PushUnorderedAsync(string peerId, ReadOnlyMemory<byte> encoded, CallScope scope)
    {
        var outcome = await OpenAsync(peerId, QuicStreamType.Unidirectional, scope);
        return outcome switch
        {
            StreamLease lease => await SendOneShotAsync(lease, encoded, scope),
            QuicFault fault => fault,
        };
    }

    private async ValueTask<PushDeliveryOutcome> SendOneShotAsync(StreamLease lease, ReadOnlyMemory<byte> encoded, CallScope scope)
    {
        _metrics.StreamOpened(QuicSide.Client);
        var aborted = false;
        try
        {
            var written = await QuicIo.WriteAsync(lease.Stream, encoded, true, scope);
            return written switch
            {
                QuicSent done => SentPush(done, encoded.Length),
                QuicFault fault => AbortPush(lease, fault, out aborted),
            };
        }
        finally
        {
            if (aborted)
            {
                _ = QuicIo.DisposeStreamAsync(lease.Stream);
            }
            else
            {
                await QuicIo.DisposeStreamAsync(lease.Stream);
            }

            _metrics.StreamClosed(QuicSide.Client);
        }
    }

    private PushDeliveryOutcome SentPush(QuicSent done, int length)
    {
        _metrics.Sent(QuicSide.Client, length);
        return done;
    }

    private QuicFault AbortPush(StreamLease lease, QuicFault fault, out bool aborted)
    {
        aborted = true;
        lease.Stream.Abort(QuicAbortDirection.Write, QuicProtocol.StreamErrorCode);
        _pool.InvalidateIfDead(lease, fault);
        return fault;
    }

    private async ValueTask<PushDeliveryOutcome> PushOrderedAsync(string peerId, string channelKey, FrameBuilder builder, CallScope scope) =>
        Resolve(peerId) switch
        {
            PeerSlot slot => await EnqueueOrderedAsync(_pool.GetOrderedChannel(slot, channelKey), builder, scope),
            QuicFault fault => fault,
        };

    private static async ValueTask<PushDeliveryOutcome> EnqueueOrderedAsync(OrderedPushChannel? channel, FrameBuilder builder, CallScope scope)
    {
        if (channel is null)
        {
            return new TransportShutdown("The peer was removed from the QUIC client.");
        }

        var push = new PendingPush(builder.Detach(out var length), length);
        QuicFault? rejected = new TransportShutdown("The ordered push was not enqueued.");
        try
        {
            var outcome = await channel.EnqueueAsync(push, scope);
            rejected = outcome switch
            {
                PushEnqueued => null,
                QuicFault fault => fault,
            };
        }
        finally
        {
            if (rejected is not null)
            {
                push.Release();
            }
        }

        return rejected is { } refusal ? refusal : await AwaitDeliveryAsync(push, scope);
    }

    private static async ValueTask<PushDeliveryOutcome> AwaitDeliveryAsync(PendingPush push, CallScope scope)
    {
        using var registration = scope.Token.Register(() => push.Abandon(scope));
        return await push.Completion;
    }

    private ulong NextRequestId() => (ulong)Interlocked.Increment(ref _nextRequestId);
}
