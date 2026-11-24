using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class QuicRpcServerCore(
    QuicMeshOptions options,
    QuicRoutes routes,
    QuicMeshMetrics metrics,
    TimeProvider timeProvider,
    ILogger logger) : IAsyncDisposable
{
    private readonly QuicCertificateValidator _validator = new(options.TrustedAuthorities, timeProvider);
    private readonly QuicCertificateSource _certificates = new(options, timeProvider);
    private readonly CancellationTokenSource _drain = new();
    private readonly CancellationTokenSource _serverStreams = new();
    private readonly CancellationTokenSource _handlers = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly InFlightTracker _streams = new();
    private readonly InFlightTracker _connectionLoops = new();
    private readonly ConcurrentDictionary<QuicConnection, byte> _live = new();
    private readonly Lock _peerGate = new();
    private readonly Dictionary<string, int> _peerConnections = new(StringComparer.Ordinal);
    private readonly Lock _pushGate = new();
    private readonly HashSet<QuicStream> _pushStreams = [];
    private QuicListener? _listener;
    private Task _acceptLoop = Task.CompletedTask;
    private int _draining;
    private int _disposed;

    public async ValueTask<QuicHostStartOutcome> StartAsync(CancellationToken cancellationToken)
    {
        var listened = await QuicIo.ListenAsync(
            new QuicListenerOptions
            {
                ListenEndPoint = options.ListenEndPoint,
                ApplicationProtocols = [new SslApplicationProtocol(QuicProtocol.ApplicationProtocol)],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(ConnectionOptions()),
            },
            cancellationToken);
        return listened switch
        {
            QuicListener listener => Listening(listener),
            QuicFault fault => ListenFailed(fault),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _draining, 1);
        await _serverStreams.CancelAsync();
        lock (_pushGate)
        {
            foreach (var pushStream in _pushStreams)
            {
                pushStream.Abort(QuicAbortDirection.Read, QuicProtocol.PeerClosedErrorCode);
            }
        }

        await _drain.CancelAsync();
        if (_listener is { } listener)
        {
            await listener.DisposeAsync();
        }

        await _acceptLoop;
        if (!await InFlightTracker.WaitAsync(_streams.DrainAsync(), QuicMeshLimits.DrainTimeout, timeProvider))
        {
            QuicRpcLog.DrainTimedOut(logger, QuicMeshLimits.DrainTimeout);
        }

        await _handlers.CancelAsync();
        await Task.WhenAll(_live.Keys.Select(CloseConnectionBoundedAsync));
        await _closing.CancelAsync();
        _ = await InFlightTracker.WaitAsync(_connectionLoops.DrainAsync(), QuicMeshLimits.CloseTimeout, timeProvider);
        _drain.Dispose();
        _serverStreams.Dispose();
        _handlers.Dispose();
        _closing.Dispose();
    }

    private async Task CloseConnectionBoundedAsync(QuicConnection connection)
    {
        var closeTask = QuicIo.CloseAsync(connection, QuicProtocol.CloseErrorCode).AsTask();
        using var timeout = new CancellationTokenSource();
        var delay = Task.Delay(QuicMeshLimits.CloseTimeout, timeProvider, timeout.Token);
        var winner = await Task.WhenAny(closeTask, delay);
        await timeout.CancelAsync();
        if (winner == closeTask)
        {
            Observe(await closeTask);
        }
    }

    private QuicServerConnectionOptions ConnectionOptions() => new()
    {
        DefaultStreamErrorCode = QuicProtocol.StreamErrorCode,
        DefaultCloseErrorCode = QuicProtocol.CloseErrorCode,
        IdleTimeout = QuicMeshLimits.IdleTimeout,
        KeepAliveInterval = QuicMeshLimits.KeepAliveInterval,
        HandshakeTimeout = QuicMeshLimits.HandshakeTimeout,
        MaxInboundBidirectionalStreams = QuicMeshLimits.MaxInboundBidirectionalStreams,
        MaxInboundUnidirectionalStreams = QuicMeshLimits.MaxInboundUnidirectionalStreams,
        ServerAuthenticationOptions = new SslServerAuthenticationOptions
        {
            ApplicationProtocols = [new SslApplicationProtocol(QuicProtocol.ApplicationProtocol)],
            ServerCertificateContext = _certificates.Current(),
            ClientCertificateRequired = true,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
                _validator.ValidateRemote(certificate, chain, QuicCertificateValidator.ClientAuthOid) is not null,
        },
    };

    private QuicHostStartOutcome Listening(QuicListener listener)
    {
        _listener = listener;
        _acceptLoop = AcceptConnectionsAsync(listener);
        QuicRpcLog.ListenerStarted(logger, options.NodeId, options.Region, listener.LocalEndPoint);
        return listener.LocalEndPoint;
    }

    private QuicHostStartOutcome ListenFailed(QuicFault fault)
    {
        QuicRpcLog.ListenFailed(logger, options.ListenEndPoint, fault.Message);
        return new QuicUnavailable(fault.Message, "listen_failed");
    }

    private async Task AcceptConnectionsAsync(QuicListener listener)
    {
        using var scope = new CallScope(Timeout.InfiniteTimeSpan, timeProvider, _drain.Token, CancellationToken.None);
        while (!_drain.IsCancellationRequested)
        {
            var accepted = await QuicIo.AcceptConnectionAsync(listener, scope);
            var admitted = accepted switch
            {
                QuicConnection connection => await AdmitAsync(connection),
                QuicFault fault => Rejected(fault),
            };
            if (!admitted)
            {
                return;
            }
        }
    }

    private async ValueTask<bool> AdmitAsync(QuicConnection connection)
    {
        using var certificate = connection.RemoteCertificate;
        if (QuicMeshIdentity.ReadRemote(certificate) is not { } identity)
        {
            await QuicIo.DisposeConnectionAsync(connection);
            return Rejected(new PeerRejected("The peer certificate carries no mesh identity."));
        }

        if (!TryReservePeerConnection(identity.NodeId))
        {
            await QuicIo.DisposeConnectionAsync(connection);
            return Rejected(new PeerRejected($"Peer '{identity.NodeId}' reached its active connection limit."));
        }

        _connectionLoops.Enter();
        _live.TryAdd(connection, 0);
        _ = RunConnectionAsync(connection, identity);
        return true;
    }

    private bool TryReservePeerConnection(string nodeId)
    {
        lock (_peerGate)
        {
            var count = _peerConnections.GetValueOrDefault(nodeId);
            if (count >= QuicMeshLimits.MaxConnectionsPerPeer)
            {
                return false;
            }

            _peerConnections[nodeId] = count + 1;
            return true;
        }
    }

    private void ReleasePeerConnection(string nodeId)
    {
        lock (_peerGate)
        {
            var count = _peerConnections[nodeId] - 1;
            if (count == 0)
            {
                _peerConnections.Remove(nodeId);
            }
            else
            {
                _peerConnections[nodeId] = count;
            }
        }
    }

    private bool Rejected(QuicFault fault)
    {
        if (_drain.IsCancellationRequested)
        {
            return false;
        }

        metrics.Error(QuicSide.Server, fault);
        QuicRpcLog.ConnectionRejected(logger, fault.Message);
        return true;
    }

    private async Task RunConnectionAsync(QuicConnection connection, QuicPeerIdentity identity)
    {
        metrics.ConnectionOpened(QuicSide.Server, false);
        try
        {
            QuicRpcLog.PeerConnected(logger, identity.NodeId, identity.Region, connection.RemoteEndPoint);
            var streams = new InFlightTracker();
            await AcceptStreamsAsync(connection, identity, streams);
            await streams.DrainAsync();
            await CloseConnectionBoundedAsync(connection);
        }
        finally
        {
            await QuicIo.DisposeConnectionAsync(connection);
            _live.TryRemove(connection, out _);
            ReleasePeerConnection(identity.NodeId);
            metrics.ConnectionClosed(QuicSide.Server);
            _connectionLoops.Exit();
        }
    }

    private async Task AcceptStreamsAsync(QuicConnection connection, QuicPeerIdentity identity, InFlightTracker streams)
    {
        using var scope = new CallScope(Timeout.InfiniteTimeSpan, timeProvider, _closing.Token, CancellationToken.None);
        while (true)
        {
            var accepted = await QuicIo.AcceptStreamAsync(connection, scope);
            var dispatched = accepted switch
            {
                QuicStream stream => Dispatch(stream, identity, streams),
                QuicFault => false,
            };
            if (!dispatched)
            {
                return;
            }
        }
    }

    private bool Dispatch(QuicStream stream, QuicPeerIdentity identity, InFlightTracker streams)
    {
        streams.Enter();
        _streams.Enter();
        _ = RunStreamAsync(stream, identity, streams);
        return true;
    }

    private async Task RunStreamAsync(QuicStream stream, QuicPeerIdentity identity, InFlightTracker streams)
    {
        metrics.StreamOpened(QuicSide.Server);
        var bidirectional = stream.Type == QuicStreamType.Bidirectional;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_handlers.Token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = bidirectional ? CancelOnPeerAbortAsync(stream, cancellation, finished.Task) : Task.CompletedTask;
        try
        {
            await RunGuardedAsync(stream, identity, bidirectional, cancellation.Token);
        }
        finally
        {
            finished.TrySetResult();
            await watcher;
            cancellation.Dispose();
            await QuicIo.DisposeStreamAsync(stream);
            metrics.StreamClosed(QuicSide.Server);
            streams.Exit();
            _streams.Exit();
        }
    }

    private static async Task CancelOnPeerAbortAsync(QuicStream stream, CancellationTokenSource cancellation, Task finished)
    {
        if (await QuicIo.PeerStoppedReadingAsync(stream, finished))
        {
            await cancellation.CancelAsync();
        }
    }

    private async Task RunGuardedAsync(QuicStream stream, QuicPeerIdentity identity, bool bidirectional, CancellationToken cancellationToken)
    {
        var direction = bidirectional ? QuicAbortDirection.Both : QuicAbortDirection.Read;
        if (Volatile.Read(ref _draining) != 0)
        {
            stream.Abort(direction, QuicProtocol.PeerClosedErrorCode);
            return;
        }

        var handling = bidirectional
            ? HandleBidirectionalAsync(stream, identity, cancellationToken)
            : HandlePushStreamAsync(stream, identity, cancellationToken);
        await handling.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (handling.IsCanceled && cancellationToken.IsCancellationRequested)
        {
            metrics.Error(QuicSide.Server, QuicErrorKind.LocalAbort);
            stream.Abort(direction, QuicProtocol.StreamErrorCode);
        }
        else if (!handling.IsCompletedSuccessfully)
        {
            metrics.Error(QuicSide.Server, QuicErrorKind.HandlerFault);
            QuicRpcLog.HandlerFaulted(logger, handling.Exception?.GetBaseException() ?? new TaskCanceledException(handling), identity.NodeId, bidirectional);
            stream.Abort(direction, QuicProtocol.StreamErrorCode);
        }
    }

    private async Task HandleBidirectionalAsync(QuicStream stream, QuicPeerIdentity identity, CancellationToken cancellationToken)
    {
        FrameRead read;
        using (var requestScope = new CallScope(QuicMeshLimits.CallTimeout, timeProvider, cancellationToken, CancellationToken.None))
        {
            read = await QuicIo.ReadFrameAsync(stream, requestScope);
        }

        await (read switch
        {
            PooledFrame frame => RespondAsync(stream, identity, frame, cancellationToken),
            StreamEnded => Refuse(stream, new ProtocolFault("The peer closed a bidirectional stream before its request frame.")),
            QuicFault fault => Refuse(stream, fault),
        });
    }

    private Task Refuse(QuicStream stream, QuicFault fault)
    {
        metrics.Error(QuicSide.Server, fault);
        stream.Abort(QuicAbortDirection.Both, QuicProtocol.StreamErrorCode);
        return Task.CompletedTask;
    }

    private async Task RespondAsync(QuicStream stream, QuicPeerIdentity identity, PooledFrame frame, CancellationToken cancellationToken)
    {
        using (frame)
        {
            metrics.Received(QuicSide.Server, frame.TotalLength);
            var header = frame.Header;
            var context = new QuicCallContext(identity.NodeId, identity.Region, header.MessageType, header.RequestId);
            using var scope = new CallScope(Timeout.InfiniteTimeSpan, timeProvider, cancellationToken, CancellationToken.None);
            if (header.IsError || !routes.Bidi.TryGetValue(header.MessageType, out var route))
            {
                metrics.Error(QuicSide.Server, QuicErrorKind.Unmapped);
                var unmapped = new QuicRemoteError(QuicRemoteError.UnmappedMessageType, $"No unary or server-stream handler is mapped for message type {header.MessageType}.");
                var started = timeProvider.GetTimestamp();
                var written = await WriteErrorAsync(stream, context, unmapped, scope);
                Complete(context, QuicPattern.Unary, started, written, QuicOutcome.RemoteError);
                return;
            }

            await (route switch
            {
                UnaryDispatch unary => RespondUnaryAsync(stream, context, unary, frame.Payload, scope),
                StreamDispatch streaming => RespondStreamAsync(stream, context, streaming, frame.Payload, scope),
            });
        }
    }

    private async Task RespondUnaryAsync(QuicStream stream, QuicCallContext context, UnaryDispatch unary, ReadOnlyMemory<byte> payload, CallScope scope)
    {
        var started = timeProvider.GetTimestamp();
        var reply = await unary(context, payload, scope.Token);
        var (written, outcome) = reply switch
        {
            FrameBuilder builder => (await WriteBuilderAsync(stream, context, builder, QuicProtocol.ControlEndOfStream, true, scope), QuicOutcome.Ok),
            QuicRemoteError error => (await WriteErrorAsync(stream, context, error, scope), QuicOutcome.RemoteError),
        };
        Complete(context, QuicPattern.Unary, started, written, outcome);
    }

    private async Task RespondStreamAsync(QuicStream stream, QuicCallContext context, StreamDispatch streaming, ReadOnlyMemory<byte> payload, CallScope callScope)
    {
        var started = timeProvider.GetTimestamp();
        using var scope = new CallScope(Timeout.InfiniteTimeSpan, timeProvider, _serverStreams.Token, callScope.Token);
        var handling = StreamItemsAsync(stream, context, streaming, payload, scope, started);
        await handling.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (handling.IsCanceled && _serverStreams.IsCancellationRequested && !callScope.Token.IsCancellationRequested)
        {
            ServerStreamDrained(stream, context, started);
        }
        else
        {
            await handling;
        }
    }

    private async Task StreamItemsAsync(QuicStream stream, QuicCallContext context, StreamDispatch streaming, ReadOnlyMemory<byte> payload, CallScope scope, long started)
    {
        await foreach (var item in streaming(context, payload, scope.Token))
        {
            var (written, terminal) = item switch
            {
                FrameBuilder builder => (await WriteBuilderAsync(stream, context, builder, QuicProtocol.ControlNone, false, scope), false),
                QuicRemoteError error => (await WriteErrorAsync(stream, context, error, scope), true),
            };
            if (_serverStreams.IsCancellationRequested)
            {
                ServerStreamDrained(stream, context, started);
                return;
            }

            if (Fault(written) is not null)
            {
                Complete(context, QuicPattern.ServerStream, started, written, QuicOutcome.Unavailable);
                stream.Abort(QuicAbortDirection.Both, QuicProtocol.StreamErrorCode);
                return;
            }

            if (terminal)
            {
                Complete(context, QuicPattern.ServerStream, started, written, QuicOutcome.RemoteError);
                return;
            }
        }

        if (_serverStreams.IsCancellationRequested)
        {
            ServerStreamDrained(stream, context, started);
            return;
        }

        using var end = FrameBuilder.Empty();
        var ended = await WriteBuilderAsync(stream, context, end, QuicProtocol.ControlEndOfStream, true, scope);
        Complete(context, QuicPattern.ServerStream, started, ended, QuicOutcome.Ok);
    }

    private void ServerStreamDrained(QuicStream stream, QuicCallContext context, long started)
    {
        metrics.Completed(QuicSide.Server, QuicPattern.ServerStream, context.MessageTypeId, QuicOutcome.Unavailable, timeProvider.GetElapsedTime(started));
        stream.Abort(QuicAbortDirection.Both, QuicProtocol.PeerClosedErrorCode);
    }

    private async Task HandlePushStreamAsync(QuicStream stream, QuicPeerIdentity identity, CancellationToken cancellationToken)
    {
        lock (_pushGate)
        {
            _pushStreams.Add(stream);
            if (Volatile.Read(ref _draining) != 0)
            {
                stream.Abort(QuicAbortDirection.Read, QuicProtocol.PeerClosedErrorCode);
            }
        }

        try
        {
            while (true)
            {
                FrameRead read;
                using (var scope = new CallScope(QuicMeshLimits.PushReadTimeout, timeProvider, cancellationToken, CancellationToken.None))
                {
                    read = await QuicIo.ReadFrameAsync(stream, scope);
                }

                var keepReading = read switch
                {
                    PooledFrame frame => await DispatchPushAsync(stream, identity, frame, cancellationToken),
                    StreamEnded => false,
                    QuicFault fault => PushStreamFaulted(stream, fault),
                };
                if (!keepReading)
                {
                    return;
                }
            }
        }
        finally
        {
            lock (_pushGate)
            {
                _pushStreams.Remove(stream);
            }
        }
    }

    private bool PushStreamFaulted(QuicStream stream, QuicFault fault)
    {
        metrics.Error(QuicSide.Server, fault);
        stream.Abort(QuicAbortDirection.Read, QuicProtocol.StreamErrorCode);
        return false;
    }

    private async ValueTask<bool> DispatchPushAsync(QuicStream stream, QuicPeerIdentity identity, PooledFrame frame, CancellationToken cancellationToken)
    {
        using (frame)
        {
            metrics.Received(QuicSide.Server, frame.TotalLength);
            var header = frame.Header;
            var context = new QuicCallContext(identity.NodeId, identity.Region, header.MessageType, header.RequestId);
            var started = timeProvider.GetTimestamp();
            if (header.IsError || !routes.Push.TryGetValue(header.MessageType, out var dispatch))
            {
                metrics.Error(QuicSide.Server, QuicErrorKind.Unmapped);
                return RejectPush(stream, context, started, $"no push handler is mapped for message type {header.MessageType}");
            }

            var outcome = await dispatch(context, frame.Payload, cancellationToken);
            return outcome switch
            {
                QuicSent => AcceptedPush(context, started),
                QuicRemoteError error => RejectPush(stream, context, started, error.Message),
            };
        }
    }

    private bool AcceptedPush(QuicCallContext context, long started)
    {
        metrics.Completed(QuicSide.Server, QuicPattern.Push, context.MessageTypeId, QuicOutcome.Ok, timeProvider.GetElapsedTime(started));
        return true;
    }

    private bool RejectPush(QuicStream stream, QuicCallContext context, long started, string reason)
    {
        metrics.Completed(QuicSide.Server, QuicPattern.Push, context.MessageTypeId, QuicOutcome.RemoteError, timeProvider.GetElapsedTime(started));
        QuicRpcLog.PushRejected(logger, context.RemoteNodeId, context.MessageTypeId, reason);
        stream.Abort(QuicAbortDirection.Read, QuicProtocol.PushRejectedErrorCode);
        return false;
    }

    private async ValueTask<QuicIoWriteOutcome> WriteBuilderAsync(QuicStream stream, QuicCallContext context, FrameBuilder builder, byte control, bool completeWrites, CallScope scope)
    {
        using (builder)
        {
            using var writeScope = new CallScope(QuicMeshLimits.CallTimeout, timeProvider, scope.Token, CancellationToken.None);
            var encoded = builder.Seal(context.MessageTypeId, control, context.RequestId);
            var written = await QuicIo.WriteAsync(stream, encoded, completeWrites, writeScope);
            if (Fault(written) is null)
            {
                metrics.Sent(QuicSide.Server, encoded.Length);
            }

            return written;
        }
    }

    private ValueTask<QuicIoWriteOutcome> WriteErrorAsync(QuicStream stream, QuicCallContext context, QuicRemoteError error, CallScope scope) =>
        WriteBuilderAsync(stream, context, FrameBuilder.EncodeError(error), QuicProtocol.ControlEndOfStream | QuicProtocol.ControlError, true, scope);

    private void Complete(QuicCallContext context, QuicPattern pattern, long started, QuicIoWriteOutcome written, QuicOutcome outcome)
    {
        var final = Fault(written) is { } fault ? Faulted(fault) : outcome;
        metrics.Completed(QuicSide.Server, pattern, context.MessageTypeId, final, timeProvider.GetElapsedTime(started));
    }

    private QuicOutcome Faulted(QuicFault fault)
    {
        metrics.Error(QuicSide.Server, fault);
        return QuicOutcome.Unavailable;
    }

    private static QuicFault? Fault(QuicIoWriteOutcome result) => result switch
    {
        QuicSent => null,
        QuicFault fault => fault,
    };

    private void Observe(QuicConnectionCloseOutcome closed)
    {
        if (closed switch { QuicConnectionClosed => null, QuicFault failure => failure } is { } fault)
        {
            metrics.Error(QuicSide.Server, fault);
        }
    }
}
