using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TerraScale.QuicRpc;

internal enum QuicSide
{
    Client,
    Server,
}

internal enum QuicPattern
{
    Unary,
    Push,
    OrderedPush,
    ServerStream,
}

internal enum QuicOutcome
{
    Ok,
    Unavailable,
    Timeout,
    RemoteError,
    Canceled,
}

internal enum QuicErrorKind
{
    ConnectionLost,
    StreamReset,
    LocalAbort,
    Deadline,
    Shutdown,
    PeerRejected,
    Protocol,
    HandlerFault,
    Unmapped,
    PeerUnknown,
}

public sealed class QuicMeshMetrics
{
    public const string MeterName = "TerraScale.QuicRpc";

    private readonly Counter<long> _calls;
    private readonly Histogram<double> _duration;
    private readonly UpDownCounter<long> _activeStreams;
    private readonly UpDownCounter<long> _activeConnections;
    private readonly Counter<long> _connects;
    private readonly Counter<long> _reconnects;
    private readonly Counter<long> _errors;
    private readonly Counter<long> _bytes;

    public QuicMeshMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _calls = meter.CreateCounter<long>("terrascale.quic.calls", "{call}", "QUIC RPC operations completed, by side, pattern, message type and outcome.");
        _duration = meter.CreateHistogram<double>("terrascale.quic.call.duration", "s", "QUIC RPC operation duration.");
        _activeStreams = meter.CreateUpDownCounter<long>("terrascale.quic.streams.active", "{stream}", "Open QUIC RPC streams.");
        _activeConnections = meter.CreateUpDownCounter<long>("terrascale.quic.connections.active", "{connection}", "Open QUIC mesh connections.");
        _connects = meter.CreateCounter<long>("terrascale.quic.connects", "{connection}", "QUIC mesh connections established.");
        _reconnects = meter.CreateCounter<long>("terrascale.quic.reconnects", "{connection}", "QUIC mesh connections re-established to a peer after a prior connection was lost.");
        _errors = meter.CreateCounter<long>("terrascale.quic.errors", "{error}", "QUIC transport and handler errors, by side and kind.");
        _bytes = meter.CreateCounter<long>("terrascale.quic.bytes", "By", "QUIC RPC frame bytes, by side and direction.");
    }

    internal void Completed(QuicSide side, QuicPattern pattern, ushort messageType, QuicOutcome outcome, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { "quic.side", SideTag(side) },
            { "quic.pattern", PatternTag(pattern) },
            { "quic.message_type", (int)messageType },
            { "quic.outcome", OutcomeTag(outcome) },
        };
        _calls.Add(1, tags);
        _duration.Record(elapsed.TotalSeconds, tags);
    }

    internal void StreamOpened(QuicSide side) => _activeStreams.Add(1, new KeyValuePair<string, object?>("quic.side", SideTag(side)));

    internal void StreamClosed(QuicSide side) => _activeStreams.Add(-1, new KeyValuePair<string, object?>("quic.side", SideTag(side)));

    internal void ConnectionOpened(QuicSide side, bool reconnect)
    {
        var tag = new KeyValuePair<string, object?>("quic.side", SideTag(side));
        _activeConnections.Add(1, tag);
        _connects.Add(1, tag);
        if (reconnect)
        {
            _reconnects.Add(1, tag);
        }
    }

    internal void ConnectionClosed(QuicSide side) => _activeConnections.Add(-1, new KeyValuePair<string, object?>("quic.side", SideTag(side)));

    internal void Error(QuicSide side, QuicErrorKind kind) =>
        _errors.Add(1, new KeyValuePair<string, object?>("quic.side", SideTag(side)), new KeyValuePair<string, object?>("quic.error", ErrorTag(kind)));

    internal void Error(QuicSide side, QuicFault fault) => Error(side, fault switch
    {
        ConnectionLost => QuicErrorKind.ConnectionLost,
        StreamReset => QuicErrorKind.StreamReset,
        LocalAbort => QuicErrorKind.LocalAbort,
        DeadlineExceeded => QuicErrorKind.Deadline,
        TransportShutdown => QuicErrorKind.Shutdown,
        PeerRejected => QuicErrorKind.PeerRejected,
        ProtocolFault => QuicErrorKind.Protocol,
        PeerUnknown => QuicErrorKind.PeerUnknown,
    });

    internal void Sent(QuicSide side, long bytes) =>
        _bytes.Add(bytes, new KeyValuePair<string, object?>("quic.side", SideTag(side)), new KeyValuePair<string, object?>("quic.direction", "sent"));

    internal void Received(QuicSide side, long bytes) =>
        _bytes.Add(bytes, new KeyValuePair<string, object?>("quic.side", SideTag(side)), new KeyValuePair<string, object?>("quic.direction", "received"));

    private static string SideTag(QuicSide side) => side switch
    {
        QuicSide.Client => "client",
        QuicSide.Server => "server",
        _ => throw new UnreachableException(),
    };

    private static string PatternTag(QuicPattern pattern) => pattern switch
    {
        QuicPattern.Unary => "unary",
        QuicPattern.Push => "push",
        QuicPattern.OrderedPush => "ordered_push",
        QuicPattern.ServerStream => "server_stream",
        _ => throw new UnreachableException(),
    };

    private static string OutcomeTag(QuicOutcome outcome) => outcome switch
    {
        QuicOutcome.Ok => "ok",
        QuicOutcome.Unavailable => "unavailable",
        QuicOutcome.Timeout => "timeout",
        QuicOutcome.RemoteError => "remote_error",
        QuicOutcome.Canceled => "canceled",
        _ => throw new UnreachableException(),
    };

    private static string ErrorTag(QuicErrorKind kind) => kind switch
    {
        QuicErrorKind.ConnectionLost => "connection_lost",
        QuicErrorKind.StreamReset => "stream_reset",
        QuicErrorKind.LocalAbort => "local_abort",
        QuicErrorKind.Deadline => "deadline",
        QuicErrorKind.Shutdown => "shutdown",
        QuicErrorKind.PeerRejected => "peer_rejected",
        QuicErrorKind.Protocol => "protocol",
        QuicErrorKind.HandlerFault => "handler_fault",
        QuicErrorKind.Unmapped => "unmapped",
        QuicErrorKind.PeerUnknown => "peer_unknown",
        _ => throw new UnreachableException(),
    };
}
