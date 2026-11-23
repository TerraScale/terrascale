using System.Net;
using Microsoft.Extensions.Logging;

namespace TerraScale.QuicRpc;

internal static partial class QuicRpcLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "QUIC mesh host {NodeId} in region {Region} listening on {EndPoint}")]
    public static partial void ListenerStarted(ILogger logger, string nodeId, string region, IPEndPoint endPoint);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "QUIC mesh host rejected an inbound connection: {Reason}")]
    public static partial void ConnectionRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "QUIC mesh peer {NodeId} in region {Region} connected from {EndPoint}")]
    public static partial void PeerConnected(ILogger logger, string nodeId, string region, IPEndPoint endPoint);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "QUIC handler for a stream from peer {NodeId} faulted (bidirectional: {Bidirectional})")]
    public static partial void HandlerFaulted(ILogger logger, Exception exception, string nodeId, bool bidirectional);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "QUIC push stream from peer {NodeId} was rejected at message type {MessageType}: {Reason}")]
    public static partial void PushRejected(ILogger logger, string nodeId, ushort messageType, string reason);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "QUIC mesh drain did not finish within {Grace}; canceling the remaining in-flight work")]
    public static partial void DrainTimedOut(ILogger logger, TimeSpan grace);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "QUIC client connected to peer {NodeId} at {EndPoint} (reconnect: {Reconnect})")]
    public static partial void ClientConnected(ILogger logger, string nodeId, IPEndPoint endPoint, bool reconnect);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "QUIC client could not connect to peer {NodeId} at {EndPoint}: {Reason}")]
    public static partial void ClientConnectFailed(ILogger logger, string nodeId, IPEndPoint endPoint, string reason);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "QUIC mesh host failed to listen on {EndPoint}: {Reason}")]
    public static partial void ListenFailed(ILogger logger, IPEndPoint endPoint, string reason);
}
