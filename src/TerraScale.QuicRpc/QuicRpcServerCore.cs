using System.Collections.Concurrent;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class StreamAbortWatcher : IDisposable
{
    private const int MaxPoolSize = 64;

    private static readonly ConcurrentQueue<StreamAbortWatcher> Pool = new();
    private static int PoolCount;

    [ThreadStatic]
    private static StreamAbortWatcher? ThreadLocal;

    private static readonly Action<object?> CancelCallback = static state =>
    {
        if (state is StreamAbortWatcher watcher)
        {
            watcher.TryCancelCts();
        }
    };

    private readonly Lock _ctsGate = new();
    private readonly Action _onWritesClosed;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenRegistration _handlersReg;
    private Task? _writesClosed;
    private int _disposed;
    private int _cancelFired;
    private int _refCount;
    private bool _ctsRetired;

    private StreamAbortWatcher()
    {
        _onWritesClosed = OnWritesClosed;
    }

    public CancellationToken Token => _cts.Token;

    public static StreamAbortWatcher Rent(QuicStream stream, CancellationToken handlersToken)
    {
        var watcher = ThreadLocal;
        if (watcher is not null)
        {
            ThreadLocal = null;
        }
        else if (Pool.TryDequeue(out watcher))
        {
            Interlocked.Decrement(ref PoolCount);
        }
        else
        {
            watcher = new StreamAbortWatcher();
        }

        var writesClosed = stream.WritesClosed;
        var alreadyCompleted = writesClosed.IsCompleted;
        lock (watcher._ctsGate)
        {
            watcher._ctsRetired = false;
            watcher._writesClosed = alreadyCompleted ? null : writesClosed;
            Volatile.Write(ref watcher._disposed, 0);
            Volatile.Write(ref watcher._cancelFired, 0);
            Interlocked.Exchange(ref watcher._refCount, alreadyCompleted ? 1 : 2);
        }

        watcher._handlersReg = handlersToken.UnsafeRegister(CancelCallback, watcher);
        if (alreadyCompleted)
        {
            if (writesClosed.IsCanceled || writesClosed.IsFaulted)
            {
                watcher.TryCancelCts();
            }
        }
        else
        {
            writesClosed.GetAwaiter().UnsafeOnCompleted(watcher._onWritesClosed);
        }

        return watcher;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _handlersReg.Dispose();
        _handlersReg = default;
        ReleaseRef();
    }

    private void TryCancelCts()
    {
        Volatile.Write(ref _cancelFired, 1);
        lock (_ctsGate)
        {
            if (_ctsRetired || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _cts.Cancel();
        }
    }

    private void OnWritesClosed()
    {
        if (Volatile.Read(ref _disposed) == 0
            && _writesClosed is { } task
            && (task.IsCanceled || task.IsFaulted))
        {
            TryCancelCts();
        }

        ReleaseRef();
    }

    private void ReleaseRef()
    {
        if (Interlocked.Decrement(ref _refCount) != 0)
        {
            return;
        }

        var reusable = false;
        lock (_ctsGate)
        {
            _ctsRetired = true;
            _writesClosed = null;
            if (Volatile.Read(ref _cancelFired) == 0 && !_cts.IsCancellationRequested && _cts.TryReset())
            {
                reusable = true;
            }
            else
            {
                _cts.Dispose();
            }
        }

        if (!reusable)
        {
            return;
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
            _cts.Dispose();
        }
    }
}

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
    private static readonly TimeSpan DeadlineRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly QuicCertificateValidator _validator = new(options.TrustedAuthorities, timeProvider);
    private readonly QuicCertificateSource _certificates = new(options, timeProvider);
    private readonly CancellationTokenSource _drain = new();
    private readonly CancellationTokenSource _idleUnary = new();
    private readonly CancellationTokenSource _serverStreams = new();
    private readonly CancellationTokenSource _handlers = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly InFlightTracker _streams = new();
    private readonly InFlightTracker _connectionLoops = new();
    private readonly Lock _peerGate = new();
    private readonly Dictionary<string, List<PeerConnection>> _peerConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<QuicPeerIdentity, StorageFormatMismatch> _storageRefusals = new();
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
                ApplicationProtocols = QuicStorageHandshake.Protocols(options.StorageFormatId),
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
        await _idleUnary.CancelAsync();
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
        await Task.WhenAll(LiveConnections().Select(static connection => connection.CloseAsync()));
        await _closing.CancelAsync();
        _ = await InFlightTracker.WaitAsync(_connectionLoops.DrainAsync(), QuicMeshLimits.CloseTimeout, timeProvider);
        _drain.Dispose();
        _idleUnary.Dispose();
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

        await QuicIo.DisposeConnectionAsync(connection);
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
            ApplicationProtocols = QuicStorageHandshake.Protocols(options.StorageFormatId),
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
        using var scope = CallScope.FromToken(_drain.Token);
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

        var admitted = new PeerConnection(connection, CloseConnectionBoundedAsync);
        if (ReplacePeerConnection(identity.NodeId, admitted) is { } oldest)
        {
            await oldest.CloseAsync();
            await oldest.Completion;
        }

        _connectionLoops.Enter();
        admitted.Completion = RunConnectionAsync(admitted, identity);
        return true;
    }

    private PeerConnection? ReplacePeerConnection(string nodeId, PeerConnection connection)
    {
        lock (_peerGate)
        {
            if (!_peerConnections.TryGetValue(nodeId, out var connections))
            {
                _peerConnections.Add(nodeId, [connection]);
                return null;
            }

            PeerConnection? oldest = null;
            if (connections.Count == QuicMeshLimits.MaxConnectionsPerPeer)
            {
                oldest = connections[0];
                connections.RemoveAt(0);
            }

            connections.Add(connection);
            return oldest;
        }
    }

    private void ReleasePeerConnection(string nodeId, PeerConnection connection)
    {
        lock (_peerGate)
        {
            if (_peerConnections.TryGetValue(nodeId, out var connections)
                && connections.Remove(connection) && connections.Count == 0)
            {
                _peerConnections.Remove(nodeId);
            }
        }
    }

    private IReadOnlyList<PeerConnection> LiveConnections()
    {
        lock (_peerGate)
        {
            return [.. _peerConnections.Values.SelectMany(static connections => connections)];
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

    private async Task RunConnectionAsync(PeerConnection connection, QuicPeerIdentity identity)
    {
        metrics.ConnectionOpened(QuicSide.Server, false);
        try
        {
            var negotiated = connection.Transport.NegotiatedApplicationProtocol;
            if (options.StorageFormatId is { } localFormat
                && QuicStorageHandshake.RequiresStorageHandshake(negotiated))
            {
                using var scope = CallScope.Create(QuicMeshLimits.HandshakeTimeout, timeProvider, _closing.Token, CancellationToken.None);
                if (await QuicStorageHandshake.AcceptAsync(connection.Transport, localFormat, scope) is { } fault)
                {
                    metrics.Error(QuicSide.Server, fault);
                    if (ShouldLogStorageRefusal(identity, fault))
                    {
                        QuicRpcLog.StorageHandshakeRejected(logger, identity.NodeId, identity.Region, fault.Message);
                    }

                    return;
                }
            }

            _storageRefusals.TryRemove(identity, out _);
            QuicRpcLog.PeerConnected(logger, identity.NodeId, identity.Region, connection.Transport.RemoteEndPoint);
            var supportsPersistentUnary = QuicStorageHandshake.SupportsPersistentUnary(negotiated);
            await AcceptStreamsAsync(connection.Transport, identity, supportsPersistentUnary);
        }
        finally
        {
            await connection.CloseAsync();
            metrics.ConnectionClosed(QuicSide.Server);
            ReleasePeerConnection(identity.NodeId, connection);
            _connectionLoops.Exit();
        }
    }

    private bool ShouldLogStorageRefusal(QuicPeerIdentity identity, QuicFault fault) =>
        fault is not StorageFormatMismatch mismatch
        || ReferenceEquals(_storageRefusals.AddOrUpdate(
            identity,
            static (_, current) => current,
            static (_, previous, current) => previous == current ? previous : current,
            mismatch), mismatch);

    private async Task AcceptStreamsAsync(QuicConnection connection, QuicPeerIdentity identity, bool supportsPersistentUnary)
    {
        using var scope = CallScope.FromToken(_closing.Token);
        while (true)
        {
            var accepted = await QuicIo.AcceptStreamAsync(connection, scope);
            var dispatched = accepted switch
            {
                QuicStream stream => Dispatch(stream, identity, supportsPersistentUnary),
                QuicFault => false,
            };
            if (!dispatched)
            {
                return;
            }
        }
    }

    private bool Dispatch(QuicStream stream, QuicPeerIdentity identity, bool supportsPersistentUnary)
    {
        _streams.Enter();
        _ = RunStreamAsync(stream, identity, supportsPersistentUnary);
        return true;
    }

    private async Task RunStreamAsync(QuicStream stream, QuicPeerIdentity identity, bool supportsPersistentUnary)
    {
        metrics.StreamOpened(QuicSide.Server);
        var bidirectional = stream.Type == QuicStreamType.Bidirectional;
        var watcher = bidirectional ? StreamAbortWatcher.Rent(stream, _handlers.Token) : null;
        var streamToken = watcher is not null ? watcher.Token : _handlers.Token;
        try
        {
            await RunGuardedAsync(stream, identity, bidirectional, supportsPersistentUnary, streamToken);
        }
        finally
        {
            watcher?.Dispose();
            await QuicIo.DisposeStreamAsync(stream);
            metrics.StreamClosed(QuicSide.Server);
            _streams.Exit();
        }
    }

    private async Task RunGuardedAsync(QuicStream stream, QuicPeerIdentity identity, bool bidirectional, bool supportsPersistentUnary, CancellationToken cancellationToken)
    {
        var direction = bidirectional ? QuicAbortDirection.Both : QuicAbortDirection.Read;
        if (Volatile.Read(ref _draining) != 0)
        {
            stream.Abort(direction, QuicProtocol.PeerClosedErrorCode);
            return;
        }

        var handling = bidirectional
            ? HandleBidirectionalAsync(stream, identity, supportsPersistentUnary, cancellationToken)
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

    private async Task HandleBidirectionalAsync(QuicStream stream, QuicPeerIdentity identity, bool supportsPersistentUnary, CancellationToken cancellationToken)
    {
        using var reader = new QuicFrameReader(QuicFrameReader.UnaryBufferCapacity);
        var writeScope = CallScope.Create(QuicMeshLimits.CallTimeout, timeProvider, cancellationToken, CancellationToken.None);
        var idleReadScope = default(CallScope);
        try
        {
            var isSystemClock = ReferenceEquals(timeProvider, TimeProvider.System);
            var writeArmed = true;
            var writeArmedTicks = timeProvider.GetTimestamp();
            var idleArmed = false;
            var idleArmedTicks = 0L;

            var firstRead = await reader.ReadNextAsync(stream, writeScope);
            var (firstReady, firstEnded, firstFault) = UnpackRead(firstRead);
            if (!firstReady)
            {
                Refuse(stream, firstEnded
                    ? new ProtocolFault("The peer closed a bidirectional stream before its request frame.")
                    : firstFault ?? new ProtocolFault("The peer failed to send a request frame."));
                return;
            }

            var keepAlive = supportsPersistentUnary && !reader.CurrentHeader.IsEndOfStream;
            if (keepAlive)
            {
                idleReadScope = CallScope.CreateArmable(timeProvider, _idleUnary.Token, _handlers.Token);
            }

            while (true)
            {
                metrics.Received(QuicSide.Server, reader.CurrentTotalLength);
                var header = reader.CurrentHeader;
                var payload = reader.CurrentPayload;
                var context = new QuicCallContext(identity.NodeId, identity.Region, header.MessageType, header.RequestId, identity.IsPlatform);
                var closingAfterReply = !keepAlive || header.IsEndOfStream || Volatile.Read(ref _draining) != 0;

                if (header.IsError || !routes.Bidi.TryGetValue(header.MessageType, out var route))
                {
                    metrics.Error(QuicSide.Server, QuicErrorKind.Unmapped);
                    var unmapped = new QuicRemoteError(QuicRemoteError.UnmappedMessageType, $"No unary or server-stream handler is mapped for message type {header.MessageType}.");
                    var started = metrics.IsCallMetricsEnabled ? timeProvider.GetTimestamp() : 0;
                    closingAfterReply |= Volatile.Read(ref _draining) != 0;
                    EnsureDeadline(ref writeScope, QuicMeshLimits.CallTimeout, isSystemClock, ref writeArmed, ref writeArmedTicks);
                    var written = await WriteErrorWithScopeAsync(stream, context, unmapped, closingAfterReply, writeScope);
                    Complete(context, QuicPattern.Unary, started, written, QuicOutcome.RemoteError);
                    if (closingAfterReply || Fault(written) is not null)
                    {
                        if (closingAfterReply && Fault(written) is null && keepAlive && !header.IsEndOfStream)
                        {
                            stream.Abort(QuicAbortDirection.Read, QuicProtocol.PeerClosedErrorCode);
                        }

                        return;
                    }
                }
                else
                {
                    var (unaryRoute, streamRoute) = UnpackRoute(route);
                    if (streamRoute is not null)
                    {
                        if (writeArmed)
                        {
                            CallScope.Disarm(ref writeScope, timeProvider);
                            writeArmed = false;
                        }

                        await RespondStreamAsync(stream, context, streamRoute, payload, cancellationToken);
                        return;
                    }

                    if (unaryRoute is not null)
                    {
                        if (!isSystemClock && writeArmed)
                        {
                            CallScope.Disarm(ref writeScope, timeProvider);
                            writeArmed = false;
                        }

                        var started = metrics.IsCallMetricsEnabled ? timeProvider.GetTimestamp() : 0;
                        var reply = await unaryRoute(context, payload, cancellationToken);
                        closingAfterReply |= Volatile.Read(ref _draining) != 0;
                        EnsureDeadline(ref writeScope, QuicMeshLimits.CallTimeout, isSystemClock, ref writeArmed, ref writeArmedTicks);
                        var (replyBuilder, replyError) = UnpackUnaryReply(reply);
                        QuicIoWriteOutcome written;
                        QuicOutcome outcome;
                        if (replyBuilder is not null)
                        {
                            using (replyBuilder)
                            {
                                var encoded = replyBuilder.Seal(context.MessageTypeId, QuicProtocol.ControlEndOfStream, context.RequestId);
                                written = await QuicIo.WriteAsync(stream, encoded, closingAfterReply, writeScope);
                                if (Fault(written) is null)
                                {
                                    metrics.Sent(QuicSide.Server, encoded.Length);
                                }
                            }

                            outcome = QuicOutcome.Ok;
                        }
                        else
                        {
                            written = await WriteErrorWithScopeAsync(stream, context, replyError ?? new QuicRemoteError(QuicRemoteError.ProtocolViolation, "Handler produced no response."), closingAfterReply, writeScope);
                            outcome = QuicOutcome.RemoteError;
                        }

                        Complete(context, QuicPattern.Unary, started, written, outcome);
                        if (closingAfterReply || Fault(written) is not null)
                        {
                            if (closingAfterReply && Fault(written) is null && keepAlive && !header.IsEndOfStream)
                            {
                                stream.Abort(QuicAbortDirection.Read, QuicProtocol.PeerClosedErrorCode);
                            }

                            return;
                        }
                    }
                }

                if (Volatile.Read(ref _draining) != 0)
                {
                    stream.CompleteWrites();
                    stream.Abort(QuicAbortDirection.Read, QuicProtocol.PeerClosedErrorCode);
                    return;
                }

                if (!isSystemClock && writeArmed)
                {
                    CallScope.Disarm(ref writeScope, timeProvider);
                    writeArmed = false;
                }

                EnsureDeadline(ref idleReadScope, QuicMeshLimits.PushReadTimeout, isSystemClock, ref idleArmed, ref idleArmedTicks);
                var nextRead = await reader.ReadNextAsync(stream, idleReadScope);
                var (nextReady, nextEnded, nextFault) = UnpackRead(nextRead);
                if (nextReady)
                {
                    if (!isSystemClock)
                    {
                        CallScope.Disarm(ref idleReadScope, timeProvider);
                        idleArmed = false;
                    }

                    continue;
                }

                if (nextEnded)
                {
                    stream.CompleteWrites();
                    return;
                }

                if (Volatile.Read(ref _draining) != 0 || nextFault is TransportShutdown)
                {
                    stream.CompleteWrites();
                    stream.Abort(QuicAbortDirection.Read, QuicProtocol.PeerClosedErrorCode);
                    return;
                }

                if (nextFault is DeadlineExceeded)
                {
                    stream.CompleteWrites();
                    stream.Abort(QuicAbortDirection.Read, QuicProtocol.StreamErrorCode);
                    return;
                }

                if (nextFault is not null)
                {
                    Refuse(stream, nextFault);
                }

                return;
            }
        }
        finally
        {
            idleReadScope.Dispose();
            writeScope.Dispose();
        }
    }

    private static (bool Ready, bool Ended, QuicFault? Fault) UnpackRead(StreamFrameStatus status) => status switch
    {
        FrameReady => (true, false, null),
        StreamEnded => (false, true, null),
        QuicFault fault => (false, false, fault),
    };

    private static (UnaryDispatch? Unary, StreamDispatch? Stream) UnpackRoute(BidiRoute route) => route switch
    {
        UnaryDispatch unary => (unary, null),
        StreamDispatch streaming => (null, streaming),
    };

    private static (FrameBuilder? Builder, QuicRemoteError? Error) UnpackUnaryReply(UnaryDispatchOutcome outcome) => outcome switch
    {
        FrameBuilder builder => (builder, null),
        QuicRemoteError error => (null, error),
    };

    private static QuicRemoteError? UnpackPushOutcome(PushDispatchOutcome outcome) => outcome switch
    {
        QuicSent => null,
        QuicRemoteError error => error,
    };

    private void EnsureDeadline(ref CallScope scope, TimeSpan timeout, bool isSystemClock, ref bool armed, ref long armedTicks)
    {
        if (!armed || !isSystemClock || scope.IsDeadlineExpired || timeProvider.GetElapsedTime(armedTicks) >= DeadlineRefreshInterval)
        {
            CallScope.ReArm(ref scope, timeout, timeProvider);
            armed = true;
            armedTicks = timeProvider.GetTimestamp();
        }
    }

    private void Refuse(QuicStream stream, QuicFault fault)
    {
        metrics.Error(QuicSide.Server, fault);
        stream.Abort(QuicAbortDirection.Both, QuicProtocol.StreamErrorCode);
    }

    private async ValueTask RespondStreamAsync(
        QuicStream stream,
        QuicCallContext context,
        StreamDispatch streaming,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var started = metrics.IsCallMetricsEnabled ? timeProvider.GetTimestamp() : 0;
        using var scope = CallScope.Create(Timeout.InfiniteTimeSpan, timeProvider, _serverStreams.Token, cancellationToken);
        var handling = StreamItemsAsync(stream, context, streaming, payload, scope, started);
        await handling.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (handling.IsCanceled && _serverStreams.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
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
        if (metrics.IsCallMetricsEnabled)
        {
            metrics.Completed(QuicSide.Server, QuicPattern.ServerStream, context.MessageTypeId, QuicOutcome.Unavailable, timeProvider.GetElapsedTime(started));
        }

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
            using var reader = new QuicFrameReader();
            var isSystemClock = ReferenceEquals(timeProvider, TimeProvider.System);
            var scope = CallScope.CreateArmable(timeProvider, cancellationToken, CancellationToken.None);
            var armed = false;
            var armedTicks = 0L;
            try
            {
                while (true)
                {
                    StreamFrameStatus read;
                    if (!cancellationToken.IsCancellationRequested && reader.TryReadBuffered(out var buffered))
                    {
                        read = buffered;
                    }
                    else
                    {
                        EnsureDeadline(ref scope, QuicMeshLimits.PushReadTimeout, isSystemClock, ref armed, ref armedTicks);
                        read = await reader.ReadNextAsync(stream, scope);
                    }

                    var (ready, ended, readFault) = UnpackRead(read);
                    if (!ready)
                    {
                        if (!ended && readFault is not null)
                        {
                            _ = PushStreamFaulted(stream, readFault);
                        }

                        return;
                    }

                    if (!isSystemClock && armed)
                    {
                        CallScope.Disarm(ref scope, timeProvider);
                        armed = false;
                    }

                    metrics.Received(QuicSide.Server, reader.CurrentTotalLength);
                    var header = reader.CurrentHeader;
                    var context = new QuicCallContext(identity.NodeId, identity.Region, header.MessageType, header.RequestId, identity.IsPlatform);
                    var started = metrics.IsCallMetricsEnabled ? timeProvider.GetTimestamp() : 0;
                    if (header.IsError || !routes.Push.TryGetValue(header.MessageType, out var dispatch))
                    {
                        metrics.Error(QuicSide.Server, QuicErrorKind.Unmapped);
                        _ = RejectPush(stream, context, started, $"no push handler is mapped for message type {header.MessageType}");
                        return;
                    }

                    var outcome = await dispatch(context, reader.CurrentPayload, cancellationToken);
                    var pushError = UnpackPushOutcome(outcome);
                    var keepReading = pushError is null
                        ? AcceptedPush(context, started)
                        : RejectPush(stream, context, started, pushError.Message);
                    if (!keepReading)
                    {
                        return;
                    }
                }
            }
            finally
            {
                scope.Dispose();
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

    private bool AcceptedPush(QuicCallContext context, long started)
    {
        if (metrics.IsCallMetricsEnabled)
        {
            metrics.Completed(QuicSide.Server, QuicPattern.Push, context.MessageTypeId, QuicOutcome.Ok, timeProvider.GetElapsedTime(started));
        }

        return true;
    }

    private bool RejectPush(QuicStream stream, QuicCallContext context, long started, string reason)
    {
        if (metrics.IsCallMetricsEnabled)
        {
            metrics.Completed(QuicSide.Server, QuicPattern.Push, context.MessageTypeId, QuicOutcome.RemoteError, timeProvider.GetElapsedTime(started));
        }

        QuicRpcLog.PushRejected(logger, context.RemoteNodeId, context.MessageTypeId, reason);
        stream.Abort(QuicAbortDirection.Read, QuicProtocol.PushRejectedErrorCode);
        return false;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<QuicIoWriteOutcome>))]
    private async ValueTask<QuicIoWriteOutcome> WriteBuilderWithScopeAsync(
        QuicStream stream,
        QuicCallContext context,
        FrameBuilder builder,
        byte control,
        bool completeWrites,
        CallScope writeScope)
    {
        using (builder)
        {
            var encoded = builder.Seal(context.MessageTypeId, control, context.RequestId);
            var written = await QuicIo.WriteAsync(stream, encoded, completeWrites, writeScope);
            if (Fault(written) is null)
            {
                metrics.Sent(QuicSide.Server, encoded.Length);
            }

            return written;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<QuicIoWriteOutcome>))]
    private async ValueTask<QuicIoWriteOutcome> WriteBuilderAsync(
        QuicStream stream,
        QuicCallContext context,
        FrameBuilder builder,
        byte control,
        bool completeWrites,
        CallScope scope)
    {
        using (builder)
        {
            using var writeScope = CallScope.Create(QuicMeshLimits.CallTimeout, timeProvider, scope.Token, CancellationToken.None);
            var encoded = builder.Seal(context.MessageTypeId, control, context.RequestId);
            var written = await QuicIo.WriteAsync(stream, encoded, completeWrites, writeScope);
            if (Fault(written) is null)
            {
                metrics.Sent(QuicSide.Server, encoded.Length);
            }

            return written;
        }
    }

    private ValueTask<QuicIoWriteOutcome> WriteErrorWithScopeAsync(QuicStream stream, QuicCallContext context, QuicRemoteError error, bool completeWrites, CallScope writeScope) =>
        WriteBuilderWithScopeAsync(stream, context, FrameBuilder.EncodeError(error), QuicProtocol.ControlEndOfStream | QuicProtocol.ControlError, completeWrites, writeScope);

    private ValueTask<QuicIoWriteOutcome> WriteErrorAsync(QuicStream stream, QuicCallContext context, QuicRemoteError error, CallScope scope) =>
        WriteBuilderAsync(stream, context, FrameBuilder.EncodeError(error), QuicProtocol.ControlEndOfStream | QuicProtocol.ControlError, true, scope);

    private void Complete(QuicCallContext context, QuicPattern pattern, long started, QuicIoWriteOutcome written, QuicOutcome outcome)
    {
        var final = Fault(written) is { } fault ? Faulted(fault) : outcome;
        if (metrics.IsCallMetricsEnabled)
        {
            metrics.Completed(QuicSide.Server, pattern, context.MessageTypeId, final, timeProvider.GetElapsedTime(started));
        }
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

    private sealed class PeerConnection(QuicConnection transport, Func<QuicConnection, Task> close)
    {
        private readonly Lazy<Task> _close = new(() => close(transport));

        public QuicConnection Transport { get; } = transport;

        public Task Completion { get; set; } = Task.CompletedTask;

        public Task CloseAsync() => _close.Value;
    }
}
