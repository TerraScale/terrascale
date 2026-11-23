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

    public static Unavailable ToUnavailable(QuicFault fault) => fault switch
    {
        ConnectionLost lost => new Unavailable(lost.Message, "peer_unreachable"),
        StreamReset reset when reset.ErrorCode == QuicProtocol.PeerClosedErrorCode => new Unavailable(reset.Message, "peer_draining"),
        StreamReset reset => new Unavailable(reset.Message, "stream_reset"),
        LocalAbort aborted => new Unavailable(aborted.Message, "stream_aborted"),
        DeadlineExceeded exceeded => new Unavailable(exceeded.Message, "request_timeout"),
        TransportShutdown shutdown => new Unavailable(shutdown.Message, "transport_shutdown"),
        PeerRejected rejected => new Unavailable(rejected.Message, "peer_unauthenticated"),
        ProtocolFault protocol => new Unavailable(protocol.Message, QuicRemoteError.ProtocolViolation),
        PeerUnknown unknown => new Unavailable(unknown.Message, "peer_unknown"),
    };

    public static Result<T, Unavailable, RequestTimeout, QuicRemoteError> ToCallFailure<T>(QuicFault fault) => fault switch
    {
        DeadlineExceeded exceeded => new RequestTimeout(exceeded.Message),
        ProtocolFault protocol => new QuicRemoteError(QuicRemoteError.ProtocolViolation, protocol.Message),
        ConnectionLost or StreamReset or LocalAbort or TransportShutdown or PeerRejected or PeerUnknown => ToUnavailable(fault),
    };

    public static Result<Done, Unavailable, RequestTimeout> ToPushFailure(QuicFault fault) => fault switch
    {
        DeadlineExceeded exceeded => new RequestTimeout(exceeded.Message),
        ConnectionLost or StreamReset or LocalAbort or TransportShutdown or PeerRejected or ProtocolFault or PeerUnknown => ToUnavailable(fault),
    };
}
