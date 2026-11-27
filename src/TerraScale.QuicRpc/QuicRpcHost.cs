using System.Collections.Frozen;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace TerraScale.QuicRpc;

internal readonly union UnaryDispatchOutcome(FrameBuilder, QuicRemoteError);

internal readonly union StreamDispatchOutcome(FrameBuilder, QuicRemoteError);

public readonly union PushDispatchOutcome(QuicSent, QuicRemoteError);

internal delegate ValueTask<UnaryDispatchOutcome> UnaryDispatch(QuicCallContext context, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

internal delegate IAsyncEnumerable<StreamDispatchOutcome> StreamDispatch(QuicCallContext context, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

internal delegate ValueTask<PushDispatchOutcome> PushDispatch(QuicCallContext context, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

internal readonly union BidiRoute(UnaryDispatch, StreamDispatch);

internal sealed record class QuicRoutes(FrozenDictionary<ushort, BidiRoute> Bidi, FrozenDictionary<ushort, PushDispatch> Push);

public sealed class QuicRpcHost : IAsyncDisposable
{
    private readonly QuicMeshOptions _options;
    private readonly QuicMeshMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QuicRpcHost> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<ushort, BidiRoute> _bidi = [];
    private readonly Dictionary<ushort, PushDispatch> _push = [];
    private QuicRpcServerCore? _core;
    private IPEndPoint? _boundEndPoint;
    private bool _started;
    private bool _disposed;

    public QuicRpcHost(QuicMeshOptions options, QuicMeshMetrics metrics, TimeProvider timeProvider, ILogger<QuicRpcHost> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Validate(timeProvider);
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string NodeId => _options.NodeId;

    public string Region => _options.Region;

    public IPEndPoint? BoundEndPoint
    {
        get
        {
            lock (_gate)
            {
                return _boundEndPoint;
            }
        }
    }

    public QuicRpcHost MapUnary<TReq, TRes>(ushort messageTypeId, Func<QuicCallContext, TReq, CancellationToken, ValueTask<QuicHandlerOutcome<TRes>>> handler)
        where TReq : IBinaryMessage<TReq>
        where TRes : IBinaryMessage<TRes>
    {
        ArgumentNullException.ThrowIfNull(handler);
        UnaryDispatch dispatch = async (context, payload, cancellationToken) =>
        {
            if (!BinaryMessage.TryDecode<TReq>(payload.Span, out var request))
            {
                return Malformed(context);
            }

            var outcome = await handler(context, request, cancellationToken);
            return outcome switch
            {
                TRes response => FrameBuilder.Encode(response),
                QuicRemoteError error => error,
            };
        };
        Register(messageTypeId, dispatch);
        return this;
    }

    public QuicRpcHost MapServerStream<TReq, TRes>(ushort messageTypeId, Func<QuicCallContext, TReq, CancellationToken, IAsyncEnumerable<QuicHandlerOutcome<TRes>>> handler)
        where TReq : IBinaryMessage<TReq>
        where TRes : IBinaryMessage<TRes>
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(messageTypeId, (StreamDispatch)((context, payload, cancellationToken) => AdaptStream(handler, context, payload, cancellationToken)));
        return this;
    }

    public QuicRpcHost MapPush<TMsg>(ushort messageTypeId, Func<QuicCallContext, TMsg, CancellationToken, ValueTask<PushDispatchOutcome>> handler)
        where TMsg : IBinaryMessage<TMsg>
    {
        ArgumentNullException.ThrowIfNull(handler);
        PushDispatch dispatch = async (context, payload, cancellationToken) =>
        {
            if (!BinaryMessage.TryDecode<TMsg>(payload.Span, out var message))
            {
                return Malformed(context);
            }

            return await handler(context, message, cancellationToken);
        };
        lock (_gate)
        {
            EnsureConfigurable();
            if (!_push.TryAdd(messageTypeId, dispatch))
            {
                throw new InvalidOperationException($"A push handler for message type {messageTypeId} is already mapped.");
            }
        }

        return this;
    }

    public async ValueTask<QuicHostStartOutcome> StartAsync(CancellationToken cancellationToken)
    {
        if (!QuicPlatform.IsSupported)
        {
            return new QuicUnavailable("System.Net.Quic is unavailable: libmsquic.so.2 (MsQuic 2.x) could not be loaded.", "quic_unsupported");
        }

        QuicRoutes routes;
        lock (_gate)
        {
            EnsureConfigurable();
            _started = true;
            routes = new QuicRoutes(_bidi.ToFrozenDictionary(), _push.ToFrozenDictionary());
        }

        var core = new QuicRpcServerCore(_options, routes, _metrics, _timeProvider, _logger);
        var outcome = await core.StartAsync(cancellationToken);
        return outcome switch
        {
            IPEndPoint bound => await AttachAsync(core, bound),
            QuicUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask DisposeAsync()
    {
        QuicRpcServerCore? core;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            core = _core;
            _core = null;
        }

        if (core is not null && QuicPlatform.IsSupported)
        {
            await core.DisposeAsync();
        }
    }

    private static QuicRemoteError Malformed(QuicCallContext context) =>
        new(QuicRemoteError.MalformedRequest, $"The payload for message type {context.MessageTypeId} is malformed.");

    private static async IAsyncEnumerable<StreamDispatchOutcome> AdaptStream<TReq, TRes>(
        Func<QuicCallContext, TReq, CancellationToken, IAsyncEnumerable<QuicHandlerOutcome<TRes>>> handler,
        QuicCallContext context,
        ReadOnlyMemory<byte> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TReq : IBinaryMessage<TReq>
        where TRes : IBinaryMessage<TRes>
    {
        if (!BinaryMessage.TryDecode<TReq>(payload.Span, out var request))
        {
            yield return Malformed(context);
            yield break;
        }

        await foreach (var item in handler(context, request, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return item switch
            {
                TRes response => FrameBuilder.Encode(response),
                QuicRemoteError error => error,
            };
        }
    }

    private async ValueTask<QuicHostStartOutcome> AttachAsync(QuicRpcServerCore core, IPEndPoint bound)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _core = core;
                _boundEndPoint = bound;
                return bound;
            }
        }

        if (QuicPlatform.IsSupported)
        {
            await core.DisposeAsync();
        }

        return new QuicUnavailable("The QUIC host was disposed while starting.", "host_disposed");
    }

    private void Register(ushort messageTypeId, BidiRoute route)
    {
        lock (_gate)
        {
            EnsureConfigurable();
            if (!_bidi.TryAdd(messageTypeId, route))
            {
                throw new InvalidOperationException($"A unary or server-stream handler for message type {messageTypeId} is already mapped.");
            }
        }
    }

    private void EnsureConfigurable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("Handlers must be mapped before StartAsync; a started QuicRpcHost cannot be remapped or restarted.");
        }
    }
}
