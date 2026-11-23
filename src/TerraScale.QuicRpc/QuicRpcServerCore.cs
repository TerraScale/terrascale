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
    private readonly CancellationTokenSource _drain = new();
    private readonly CancellationTokenSource _handlers = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly InFlightTracker _streams = new();
    private readonly InFlightTracker _connectionLoops = new();
    private readonly ConcurrentDictionary<QuicConnection, byte> _live = new();
    private readonly Lock _pushGate = new();
    private readonly HashSet<QuicStream> _pushStreams = [];
    private QuicListener? _listener;
    private Task _acceptLoop = Task.CompletedTask;
    private int _draining;
    private int _disposed;

    public async ValueTask<Result<IPEndPoint, Unavailable>> StartAsync(CancellationToken cancellationToken)
    {
        var connectionOptions = ConnectionOptions();
        var listened = await QuicIo.ListenAsync(
            new QuicListenerOptions
            {
                ListenEndPoint = options.ListenEndPoint,
                ApplicationProtocols = [new SslApplicationProtocol(QuicProtocol.ApplicationProtocol)],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(connectionOptions),
            },
            cancellationToken).ConfigureAwait(false);
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
        lock (_pushGate)
        {
            foreach (var pushStream in _pushStreams)
            {
                pushStream.Abort(QuicAbortDirection.Read, QuicProtocol.PeerClosedErrorCode);
            }
        }

        await _drain.CancelAsync().ConfigureAwait(false);
        if (_listener is { } listener)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        await _acceptLoop.ConfigureAwait(false);
        if (!await InFlightTracker.WaitAsync(_streams.DrainAsync(), QuicMeshLimits.DrainTimeout, timeProvider).ConfigureAwait(false))
        {
            QuicRpcLog.DrainTimedOut(logger, QuicMeshLimits.DrainTimeout);
        }

        await _handlers.CancelAsync().ConfigureAwait(false);
        foreach (var connection in _live.Keys)
        {
            Observe(await QuicIo.CloseAsync(connection, QuicProtocol.CloseErrorCode).ConfigureAwait(false));
        }

        await _closing.CancelAsync().ConfigureAwait(false);
        await _connectionLoops.DrainAsync().ConfigureAwait(false);
        _drain.Dispose();
        _handlers.Dispose();
        _closing.Dispose();
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
            ServerCertificateContext = SslStreamCertificateContext.Create(options.Certificate, [.. options.IntermediateCertificates], offline: true),
            ClientCertificateRequired = true,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
                _validator.ValidateRemote(certificate, chain, QuicCertificateValidator.ClientAuthOid) is not null,
        },
    };

    private Result<IPEndPoint, Unavailable> Listening(QuicListener listener)
    {
        _listener = listener;
        _acceptLoop = AcceptConnectionsAsync(listener);
        QuicRpcLog.ListenerStarted(logger, options.NodeId, options.Region, listener.LocalEndPoint);
        return listener.LocalEndPoint;
    }

    private Result<IPEndPoint, Unavailable> ListenFailed(QuicFault fault)
    {
        QuicRpcLog.ListenFailed(logger, options.ListenEndPoint, fault.Message);
        return new Unavailable(fault.Message, "listen_failed");
    }

    private async Task AcceptConnectionsAsync(QuicListener listener)
    {
        using var scope = new CallScope(Timeout.InfiniteTimeSpan, timeProvider, _drain.Token, CancellationToken.None);
        while (!_drain.IsCancellationRequested)
        {
            var accepted = await QuicIo.AcceptConnectionAsync(listener, scope).ConfigureAwait(false);
            var admitted = accepted switch
            {
                QuicConnection connection => Admit(connection),
                QuicFault fault => Rejected(fault),
            };
            if (!admitted)
            {
                return;
            }
        }
    }

    private bool Admit(QuicConnection connection)
    {
        _connectionLoops.Enter();
        _live.TryAdd(connection, 0);
        _ = RunConnectionAsync(connection);
        return true;
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

    private async Task RunConnectionAsync(QuicConnection connection)
    {
        try
        {
            await using (connection.ConfigureAwait(false))
            {
                if (QuicMeshIdentity.ReadRemote(connection.RemoteCertificate) is not { } identity)
                {
                    metrics.Error(QuicSide.Server, QuicErrorKind.PeerRejected);
                    QuicRpcLog.ConnectionRejected(logger, "the peer certificate carries no mesh identity");
                    Observe(await QuicIo.CloseAsync(connection, QuicProtocol.CloseErrorCode).ConfigureAwait(false));
                    return;
                }

                metrics.ConnectionOpened(QuicSide.Server, false);
                QuicRpcLog.PeerConnected(logger, identity.NodeId, identity.Region, connection.RemoteEndPoint);
                var streams = new InFlightTracker();
                await AcceptStreamsAsync(connection, identity, streams).ConfigureAwait(false);
                await streams.DrainAsync().ConfigureAwait(false);
                Observe(await QuicIo.CloseAsync(connection, QuicProtocol.CloseErrorCode).ConfigureAwait(false));
                metrics.ConnectionClosed(QuicSide.Server);
            }
        }
        finally
        {
            _live.TryRemove(connection, out _);
            _connectionLoops.Exit();
        }
    }

    private async Task AcceptStreamsAsync(QuicConnection connection, QuicPeerIdentity identity, InFlightTracker streams)
    {
        using var scope = new CallScope(Timeout.InfiniteTimeSpan, timeProvider, _closing.Token, CancellationToken.None);
        while (true)
        {
            var accepted = await QuicIo.AcceptStreamAsync(connection, scope).ConfigureAwait(false);
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
            await RunGuardedAsync(stream, identity, bidirectional, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            finished.TrySetResult();
            await watcher.ConfigureAwait(false);
            cancellation.Dispose();
            await stream.DisposeAsync().ConfigureAwait(false);
            metrics.StreamClosed(QuicSide.Server);
            streams.Exit();
            _streams.Exit();
        }
    }

    private static async Task CancelOnPeerAbortAsync(QuicStream stream, CancellationTokenSource cancellation, Task finished)
    {
        if (await QuicIo.PeerStoppedReadingAsync(stream, finished).ConfigureAwait(false))
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
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

        try
        {
            if (bidirectional)
            {
                await HandleBidirectionalAsync(stream, identity, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await HandlePushStreamAsync(stream, identity, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            metrics.Error(QuicSide.Server, QuicErrorKind.LocalAbort);
            stream.Abort(direction, QuicProtocol.StreamErrorCode);
        }
        catch (Exception exception)
        {
            metrics.Error(QuicSide.Server, QuicErrorKind.HandlerFault);
            QuicRpcLog.HandlerFaulted(logger, exception, identity.NodeId, bidirectional);
            stream.Abort(direction, QuicProtocol.StreamErrorCode);
        }
    }

    private async Task HandleBidirectionalAsync(QuicStream stream, QuicPeerIdentity identity, CancellationToken cancellationToken)
    {
        FrameRead read;
        using (var requestScope = new CallScope(QuicMeshLimits.CallTimeout, timeProvider, cancellationToken, CancellationToken.None))
        {
            read = await QuicIo.ReadFrameAsync(stream, requestScope).ConfigureAwait(false);
        }

        await (read switch
        {
            PooledFrame frame => RespondAsync(stream, identity, frame, cancellationToken),
            StreamEnded => Refuse(stream, new ProtocolFault("The peer closed a bidirectional stream before its request frame.")),
            QuicFault fault => Refuse(stream, fault),
        }).ConfigureAwait(false);
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
                Complete(context, QuicPattern.Unary, timeProvider.GetTimestamp(), await WriteErrorAsync(stream, context, unmapped, scope).ConfigureAwait(false), QuicOutcome.RemoteError);
                return;
            }

            await (route switch
            {
                UnaryDispatch unary => RespondUnaryAsync(stream, context, unary, frame.Payload, scope),
                StreamDispatch streaming => RespondStreamAsync(stream, context, streaming, frame.Payload, scope),
            }).ConfigureAwait(false);
        }
    }

    private async Task RespondUnaryAsync(QuicStream stream, QuicCallContext context, UnaryDispatch unary, ReadOnlyMemory<byte> payload, CallScope scope)
    {
        var started = timeProvider.GetTimestamp();
        var reply = await unary(context, payload, scope.Token).ConfigureAwait(false);
        var (written, outcome) = reply switch
        {
            FrameBuilder builder => (await WriteBuilderAsync(stream, context, builder, QuicProtocol.ControlEndOfStream, true, scope).ConfigureAwait(false), QuicOutcome.Ok),
            QuicRemoteError error => (await WriteErrorAsync(stream, context, error, scope).ConfigureAwait(false), QuicOutcome.RemoteError),
        };
        Complete(context, QuicPattern.Unary, started, written, outcome);
    }

    private async Task RespondStreamAsync(QuicStream stream, QuicCallContext context, StreamDispatch streaming, ReadOnlyMemory<byte> payload, CallScope scope)
    {
        var started = timeProvider.GetTimestamp();
        await foreach (var item in streaming(context, payload, scope.Token).ConfigureAwait(false))
        {
            var (written, terminal) = item switch
            {
                FrameBuilder builder => (await WriteBuilderAsync(stream, context, builder, QuicProtocol.ControlNone, false, scope).ConfigureAwait(false), false),
                QuicRemoteError error => (await WriteErrorAsync(stream, context, error, scope).ConfigureAwait(false), true),
            };
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

        using var end = FrameBuilder.Empty();
        var ended = await WriteBuilderAsync(stream, context, end, QuicProtocol.ControlEndOfStream, true, scope).ConfigureAwait(false);
        Complete(context, QuicPattern.ServerStream, started, ended, QuicOutcome.Ok);
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
            using var scope = new CallScope(Timeout.InfiniteTimeSpan, timeProvider, cancellationToken, CancellationToken.None);
            while (true)
            {
                var read = await QuicIo.ReadFrameAsync(stream, scope).ConfigureAwait(false);
                var keepReading = read switch
                {
                    PooledFrame frame => await DispatchPushAsync(stream, identity, frame, cancellationToken).ConfigureAwait(false),
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

            return await dispatch(context, frame.Payload, cancellationToken).ConfigureAwait(false) switch
            {
                Done => AcceptedPush(context, started),
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

    private async ValueTask<Result<Done, QuicFault>> WriteBuilderAsync(QuicStream stream, QuicCallContext context, FrameBuilder builder, byte control, bool completeWrites, CallScope scope)
    {
        using (builder)
        {
            var encoded = builder.Seal(context.MessageTypeId, control, context.RequestId);
            var written = await QuicIo.WriteAsync(stream, encoded, completeWrites, scope).ConfigureAwait(false);
            if (Fault(written) is null)
            {
                metrics.Sent(QuicSide.Server, encoded.Length);
            }

            return written;
        }
    }

    private ValueTask<Result<Done, QuicFault>> WriteErrorAsync(QuicStream stream, QuicCallContext context, QuicRemoteError error, CallScope scope) =>
        WriteBuilderAsync(stream, context, FrameBuilder.EncodeError(error), QuicProtocol.ControlEndOfStream | QuicProtocol.ControlError, true, scope);

    private void Complete(QuicCallContext context, QuicPattern pattern, long started, Result<Done, QuicFault> written, QuicOutcome outcome)
    {
        var final = Fault(written) is { } fault ? Faulted(fault) : outcome;
        metrics.Completed(QuicSide.Server, pattern, context.MessageTypeId, final, timeProvider.GetElapsedTime(started));
    }

    private QuicOutcome Faulted(QuicFault fault)
    {
        metrics.Error(QuicSide.Server, fault);
        return QuicOutcome.Unavailable;
    }

    private static QuicFault? Fault(Result<Done, QuicFault> result) => result switch
    {
        Done => null,
        QuicFault fault => fault,
    };

    private void Observe(Result<Done, QuicFault> closed)
    {
        if (Fault(closed) is { } fault)
        {
            metrics.Error(QuicSide.Server, fault);
        }
    }
}
