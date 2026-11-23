using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

public sealed record QuicRemoteError(string Code, string Message)
{
    public const string UnmappedMessageType = "unmapped_message_type";
    public const string MalformedRequest = "malformed_request";
    public const string ProtocolViolation = "protocol_violation";
}

internal closed record class QuicFault(string Message);

internal sealed record class ConnectionLost(string Message) : QuicFault(Message);

internal sealed record class StreamReset(long ErrorCode, string Message) : QuicFault(Message);

internal sealed record class LocalAbort(string Message) : QuicFault(Message);

internal sealed record class DeadlineExceeded(string Message) : QuicFault(Message);

internal sealed record class TransportShutdown(string Message) : QuicFault(Message);

internal sealed record class PeerRejected(string Message) : QuicFault(Message);

internal sealed record class ProtocolFault(string Message) : QuicFault(Message);

internal sealed record class PeerUnknown(string Message) : QuicFault(Message);

internal sealed record class StreamEnded;

internal readonly union FrameRead(PooledFrame, StreamEnded, QuicFault);

internal static class QuicFaults
{
    public static bool IsDeadConnection(QuicFault fault) => fault switch
    {
        ConnectionLost => true,
        StreamReset reset => reset.ErrorCode == QuicProtocol.PeerClosedErrorCode,
        LocalAbort => false,
        DeadlineExceeded => false,
        TransportShutdown => false,
        PeerRejected => true,
        ProtocolFault => false,
        PeerUnknown => false,
    };

    public static bool IsStreamGone(QuicFault fault) => fault switch
    {
        ConnectionLost => true,
        StreamReset => true,
        PeerRejected => true,
        LocalAbort => false,
        DeadlineExceeded => false,
        TransportShutdown => false,
        ProtocolFault => false,
        PeerUnknown => false,
    };

    public static QuicUnavailable ToUnavailable(QuicFault fault) => fault switch
    {
        ConnectionLost lost => new QuicUnavailable(lost.Message, "peer_unreachable"),
        StreamReset reset when reset.ErrorCode == QuicProtocol.PeerClosedErrorCode => new QuicUnavailable(reset.Message, "peer_draining"),
        StreamReset reset => new QuicUnavailable(reset.Message, "stream_reset"),
        LocalAbort aborted => new QuicUnavailable(aborted.Message, "stream_aborted"),
        DeadlineExceeded exceeded => new QuicUnavailable(exceeded.Message, "request_timeout"),
        TransportShutdown shutdown => new QuicUnavailable(shutdown.Message, "transport_shutdown"),
        PeerRejected rejected => new QuicUnavailable(rejected.Message, "peer_unauthenticated"),
        ProtocolFault protocol => new QuicUnavailable(protocol.Message, QuicRemoteError.ProtocolViolation),
        PeerUnknown unknown => new QuicUnavailable(unknown.Message, "peer_unknown"),
    };

    public static QuicCallOutcome<T> ToCallFailure<T>(QuicFault fault) => fault switch
    {
        DeadlineExceeded exceeded => new QuicTimeout(exceeded.Message),
        ProtocolFault protocol => new QuicRemoteError(QuicRemoteError.ProtocolViolation, protocol.Message),
        ConnectionLost or StreamReset or LocalAbort or TransportShutdown or PeerRejected or PeerUnknown => ToUnavailable(fault),
    };

    public static QuicPushOutcome ToPushFailure(QuicFault fault) => fault switch
    {
        DeadlineExceeded exceeded => new QuicTimeout(exceeded.Message),
        ConnectionLost or StreamReset or LocalAbort or TransportShutdown or PeerRejected or ProtocolFault or PeerUnknown => ToUnavailable(fault),
    };
}
