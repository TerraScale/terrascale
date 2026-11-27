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
    private readonly CancellationTokenSource _serverStreams = new();
    private readonly QuicConnectionPool _pool;
    private readonly InFlightTracker _inFlight = new();
    private readonly QuicMeshMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private long _nextRequestId;
    private int _disposed;

    public QuicRpcClientCore(QuicMeshOptions options, QuicMeshMetrics metrics, TimeProvider timeProvider, ILogger logger)
    {
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
        _pool = new QuicConnectionPool(options, new QuicCertificateValidator(options.TrustedAuthorities, timeProvider), metrics, timeProvider, logger, _shutdown.Token);
    }

    public QuicConnectionPool Pool => _pool;

    public InFlightTracker InFlight => _inFlight;

    public CancellationToken ServerStreamToken => _serverStreams.Token;

    public void UpsertPeer(QuicPeer peer) => _pool.Upsert(peer);

    public ValueTask RemovePeerAsync(string nodeId) => _pool.RemoveAsync(nodeId);

    public void ResetPeer(string nodeId) => _pool.Reset(nodeId);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
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
        var timing = _metrics.IsCallMetricsEnabled;
        var started = timing ? _timeProvider.GetTimestamp() : 0;
        _inFlight.Enter();
        try
        {
            using var scope = CallScope.Create(timeout ?? QuicMeshLimits.CallTimeout, _timeProvider, _pool.ShutdownToken, cancellationToken);
            using var builder = FrameBuilder.Encode(request);
            var requestId = NextRequestId();
            var resolved = Resolve(peerId);
            var (slot, lookupFault) = resolved switch
            {
                PeerSlot found => (found, (QuicFault?)null),
                QuicFault fault => ((PeerSlot?)null, fault),
            };
            QuicCallOutcome<TRes> result;
            if (lookupFault is not null || slot is null)
            {
                result = Failed<TRes>(lookupFault ?? new PeerUnknown($"Peer '{peerId}' is not registered with the QUIC client."));
            }
            else
            {
                slot.BeginUse();
                UnaryStreamSlot? rented = null;
                if (slot.CurrentActive() is { SupportsPersistentUnary: true } active)
                {
                    rented = slot.TryRentUnaryStream(active.Connection, _metrics);
                }

                if (rented is not null)
                {
                    var pooledOutcome = await ExchangePooledAsync<TRes>(rented, builder, messageTypeId, requestId, scope, allowRetry: true);
                    if (pooledOutcome is { } completed)
                    {
                        result = completed;
                    }
                    else
                    {
                        result = await AcquireAndExchangeAsync<TRes>(slot, builder, messageTypeId, requestId, scope);
                    }
                }
                else
                {
                    slot.EndUse();
                    result = await AcquireAndExchangeAsync<TRes>(slot, builder, messageTypeId, requestId, scope);
                }
            }

            if (timing)
            {
                _metrics.Completed(QuicSide.Client, QuicPattern.Unary, messageTypeId, Outcome(result), _timeProvider.GetElapsedTime(started));
            }

            return result;
        }
        finally
        {
            _inFlight.Exit();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<QuicPushOutcome>))]
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

        var timing = _metrics.IsCallMetricsEnabled;
        var started = timing ? _timeProvider.GetTimestamp() : 0;
        _inFlight.Enter();
        try
        {
            using var scope = CallScope.Create(QuicMeshLimits.CallTimeout, _timeProvider, _pool.ShutdownToken, cancellationToken);
            using var builder = FrameBuilder.Encode(message);
            var encoded = builder.Seal(messageTypeId, QuicProtocol.ControlNone, NextRequestId());
            PushDeliveryOutcome delivered;
            if (orderedChannelKey is null)
            {
                delivered = await PushUnorderedAsync(peerId, encoded, scope);
            }
            else
            {
                var lookup = Resolve(peerId);
                var (slot, lookupFault) = lookup switch
                {
                    PeerSlot found => (found, (QuicFault?)null),
                    QuicFault fault => ((PeerSlot?)null, fault),
                };
                if (lookupFault is not null)
                {
                    delivered = lookupFault;
                }
                else if (slot is null || _pool.GetOrderedChannel(slot, orderedChannelKey) is not { } channel)
                {
                    delivered = new TransportShutdown("The peer was removed from the QUIC client.");
                }
                else
                {
                    var outcome = await channel.PushAsync(encoded, scope);
                    var firstDone = UnpackOrderedPush(outcome);
                    delivered = firstDone is { } final
                        ? final
                        : await PushOrderedRetryAsync(peerId, orderedChannelKey, encoded, scope);
                }
            }

            var result = ToPushOutcome(delivered);
            if (timing)
            {
                var pattern = orderedChannelKey is null ? QuicPattern.Push : QuicPattern.OrderedPush;
                _metrics.Completed(QuicSide.Client, pattern, messageTypeId, Outcome(result), _timeProvider.GetElapsedTime(started));
            }

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

        await _serverStreams.CancelAsync();
        var drained = Task.WhenAll(_inFlight.DrainAsync(), _pool.CompleteChannelsAsync());
        if (!await InFlightTracker.WaitAsync(drained, QuicMeshLimits.DrainTimeout, _timeProvider))
        {
            QuicRpcLog.DrainTimedOut(_logger, QuicMeshLimits.DrainTimeout);
        }

        await _shutdown.CancelAsync();
        await _pool.ShutdownAsync();
        _serverStreams.Dispose();
        _shutdown.Dispose();
    }

    public PeerSlotLookup Resolve(string peerId) =>
        Volatile.Read(ref _disposed) != 0 ? new TransportShutdown("The QUIC client is disposed.")
        : _pool.TryGet(peerId, out var slot) ? slot
        : new PeerUnknown($"Peer '{peerId}' is not registered with the QUIC client.");

    public ValueTask<StreamLeaseOutcome> OpenAsync(string peerId, QuicStreamType type, CallScope scope) =>
        Resolve(peerId) switch
        {
            PeerSlot slot => _pool.OpenStreamAsync(slot, type, scope),
            QuicFault fault => ValueTask.FromResult<StreamLeaseOutcome>(fault),
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
            return DecodePayload<T>(frame.Header, frame.Payload.Span, messageTypeId, requestId);
        }
    }

    public static QuicCallOutcome<T> DecodePayload<T>(FrameHeader header, ReadOnlySpan<byte> payload, ushort messageTypeId, ulong requestId)
        where T : IBinaryMessage<T>
    {
        if (header.RequestId != requestId || header.MessageType != messageTypeId)
        {
            return new QuicRemoteError(QuicRemoteError.ProtocolViolation, $"Response frame for message type {header.MessageType} request {header.RequestId} does not match message type {messageTypeId} request {requestId}.");
        }

        if (header.IsError)
        {
            return ReadRemoteError(payload);
        }

        return BinaryMessage.TryDecode<T>(payload, out var response)
            ? response
            : new QuicRemoteError(QuicRemoteError.ProtocolViolation, $"Response payload for message type {messageTypeId} is malformed.");
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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<QuicCallOutcome<TRes>> AcquireAndExchangeAsync<TRes>(
        PeerSlot slot,
        FrameBuilder builder,
        ushort messageTypeId,
        ulong requestId,
        CallScope scope)
        where TRes : IBinaryMessage<TRes>
    {
        var acquired = await _pool.AcquireUnaryStreamAsync(slot, scope, preferPooled: false);
        return acquired switch
        {
            UnaryStreamSlot unarySlot => (await ExchangePooledAsync<TRes>(unarySlot, builder, messageTypeId, requestId, scope, allowRetry: false))
                ?? Failed<TRes>(new ConnectionLost("The QUIC unary stream closed unexpectedly.")),
            StreamLease lease => await ExchangeOneShotAsync<TRes>(lease, builder.Seal(messageTypeId, QuicProtocol.ControlEndOfStream, requestId), messageTypeId, requestId, scope),
            QuicFault fault => Failed<TRes>(fault),
        };
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<QuicCallOutcome<TRes>?> ExchangePooledAsync<TRes>(
        UnaryStreamSlot unarySlot,
        FrameBuilder builder,
        ushort messageTypeId,
        ulong requestId,
        CallScope scope,
        bool allowRetry)
        where TRes : IBinaryMessage<TRes>
    {
        var healthy = false;
        try
        {
            var encoded = builder.Seal(messageTypeId, QuicProtocol.ControlNone, requestId);
            var stream = unarySlot.Stream;
            var written = await QuicIo.WriteAsync(stream, encoded, false, scope);
            var writeFault = written switch
            {
                QuicSent => null,
                QuicFault fault => fault,
            };
            if (writeFault is not null)
            {
                _pool.InvalidateIfDead(unarySlot.Slot, unarySlot.Connection, writeFault);
                if (allowRetry && QuicFaults.IsStreamGone(writeFault))
                {
                    return null;
                }

                return Failed<TRes>(writeFault);
            }

            _metrics.Sent(QuicSide.Client, encoded.Length);
            var reader = unarySlot.Reader;
            var read = await reader.ReadNextAsync(stream, scope);
            var (ready, ended, readFault) = read switch
            {
                FrameReady => (true, false, (QuicFault?)null),
                StreamEnded => (false, true, (QuicFault?)null),
                QuicFault fault => (false, false, fault),
            };
            if (ready)
            {
                _metrics.Received(QuicSide.Client, reader.CurrentTotalLength);
                var decoded = DecodePayload<TRes>(reader.CurrentHeader, reader.CurrentPayload.Span, messageTypeId, requestId);
                var protocolOk = decoded switch
                {
                    TRes => true,
                    QuicRemoteError error => !string.Equals(error.Code, QuicRemoteError.ProtocolViolation, StringComparison.Ordinal),
                    QuicUnavailable or QuicTimeout => false,
                };
                healthy = protocolOk && unarySlot.IsHealthy;
                return decoded;
            }

            if (ended)
            {
                if (stream.WritesClosed.IsFaulted
                    && stream.WritesClosed.Exception?.InnerException is QuicException { QuicError: QuicError.StreamAborted, ApplicationErrorCode: QuicProtocol.PeerClosedErrorCode })
                {
                    _pool.InvalidateIfDead(unarySlot.Slot, unarySlot.Connection, new StreamReset(QuicProtocol.PeerClosedErrorCode, "The peer closed the unary stream while draining."));
                }

                if (allowRetry)
                {
                    return null;
                }

                return new QuicRemoteError(QuicRemoteError.ProtocolViolation, $"The peer closed request {requestId} without a response frame.");
            }

            if (readFault is not null)
            {
                _pool.InvalidateIfDead(unarySlot.Slot, unarySlot.Connection, readFault);
                if (allowRetry && readFault is StreamReset { ErrorCode: QuicProtocol.PeerClosedErrorCode })
                {
                    return null;
                }

                return Failed<TRes>(readFault);
            }

            return Failed<TRes>(new ConnectionLost("The QUIC unary stream failed unexpectedly."));
        }
        finally
        {
            _pool.ReturnUnaryStream(unarySlot, healthy);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<QuicCallOutcome<TRes>> ExchangeOneShotAsync<TRes>(
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
            var stream = lease.Stream;
            var written = await QuicIo.WriteAsync(stream, encoded, true, scope);
            var writeFault = written switch
            {
                QuicSent => null,
                QuicFault fault => fault,
            };
            if (writeFault is not null)
            {
                return FailedExchange<TRes>(lease, writeFault, out aborted);
            }

            _metrics.Sent(QuicSide.Client, encoded.Length);
            var read = await QuicIo.ReadSingleFrameAsync(stream, scope);
            return read switch
            {
                PooledFrame frame => Decode<TRes>(Received(frame), messageTypeId, requestId),
                StreamEnded => new QuicRemoteError(QuicRemoteError.ProtocolViolation, $"The peer closed request {requestId} without a response frame."),
                QuicFault fault => FailedExchange<TRes>(lease, fault, out aborted),
            };
        }
        finally
        {
            if (aborted)
            {
                lease.ReleaseFireAndForget();
            }
            else
            {
                await lease.ReleaseAsync();
            }

            _metrics.StreamClosed(QuicSide.Client);
        }
    }

    private PooledFrame Received(PooledFrame frame)
    {
        _metrics.Received(QuicSide.Client, frame.TotalLength);
        return frame;
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

    private QuicPushOutcome ToPushOutcome(PushDeliveryOutcome delivered) => delivered switch
    {
        QuicSent done => done,
        QuicFault fault => FailedPush(fault),
    };

    private QuicPushOutcome FailedPush(QuicFault fault)
    {
        _metrics.Error(QuicSide.Client, fault);
        return QuicFaults.ToPushFailure(fault);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<PushDeliveryOutcome>))]
    private async ValueTask<PushDeliveryOutcome> PushUnorderedAsync(string peerId, ReadOnlyMemory<byte> encoded, CallScope scope)
    {
        var outcome = await OpenAsync(peerId, QuicStreamType.Unidirectional, scope);
        return outcome switch
        {
            StreamLease lease => await SendOneShotAsync(lease, encoded, scope),
            QuicFault fault => fault,
        };
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<PushDeliveryOutcome>))]
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
                lease.ReleaseFireAndForget();
            }
            else
            {
                await lease.ReleaseAsync();
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

    private async ValueTask<PushDeliveryOutcome> PushOrderedRetryAsync(string peerId, string channelKey, ReadOnlyMemory<byte> encoded, CallScope scope)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var lookup = Resolve(peerId);
            if (lookup switch { PeerSlot => null, QuicFault fault => fault } is { } lookupFault)
            {
                return lookupFault;
            }

            var slot = lookup switch { PeerSlot resolved => resolved, QuicFault => null };
            if (slot is null || _pool.GetOrderedChannel(slot, channelKey) is not { } channel)
            {
                return new TransportShutdown("The peer was removed from the QUIC client.");
            }

            var outcome = await channel.PushAsync(encoded, scope);
            if (UnpackOrderedPush(outcome) is { } final)
            {
                return final;
            }
        }

        return new TransportShutdown("The ordered push was not enqueued.");
    }

    private static PushDeliveryOutcome? UnpackOrderedPush(OrderedPushOutcome outcome) => outcome switch
    {
        QuicSent sent => sent,
        QuicFault fault => fault,
        ChannelEvicted => null,
    };

    private ulong NextRequestId() => (ulong)Interlocked.Increment(ref _nextRequestId);
}
