using System.Net;
using Microsoft.Extensions.Logging;

namespace TerraBase.Cluster;

internal static partial class ClusterLog
{
    [LoggerMessage(EventId = 2100, Level = LogLevel.Information, Message = "QUIC mesh node {NodeId} in region {Region} listening on {EndPoint}")]
    public static partial void MeshStarted(ILogger logger, string nodeId, string region, IPEndPoint endPoint);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Region {Region} has a single node: in-region commits use the 1-of-1 fast path")]
    public static partial void RaftSingleNode(ILogger logger, string region);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "In-region raft for {Region} started with {Shards} shards across {Members} members")]
    public static partial void RaftStarted(ILogger logger, string region, int shards, int members);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning, Message = "Replicated in-region commit for database {DatabaseId} deferred to anti-entropy: {Reason}")]
    public static partial void RaftApplyDeferred(ILogger logger, Ulid databaseId, string reason);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Warning, Message = "Remote commits for database {DatabaseId} from {NodeId} deferred to anti-entropy: {Reason}")]
    public static partial void RemoteApplyDeferred(ILogger logger, Ulid databaseId, string nodeId, string reason);

    [LoggerMessage(EventId = 2105, Level = LogLevel.Debug, Message = "Push to {NodeId} failed: {Reason}")]
    public static partial void PushFailed(ILogger logger, string nodeId, string reason);

    [LoggerMessage(EventId = 2106, Level = LogLevel.Debug, Message = "Strong transaction {VictimTxId} wounded by higher-priority {WinnerTxId}")]
    public static partial void Wounded(ILogger logger, Ulid victimTxId, Ulid winnerTxId);

    [LoggerMessage(EventId = 2107, Level = LogLevel.Debug, Message = "Strong transaction {TxId} on database {DatabaseId} aborted with {Prepared} of {Required} regions prepared (staged released: {Released})")]
    public static partial void StrongAborted(ILogger logger, Ulid databaseId, Ulid txId, int prepared, int required, bool released);

    [LoggerMessage(EventId = 2108, Level = LogLevel.Warning, Message = "Strong commit {TxId} on database {DatabaseId} is decided but in-region quorum is unconfirmed: {Reason}")]
    public static partial void InRegionQuorumUnconfirmed(ILogger logger, Ulid databaseId, Ulid txId, string reason);

    [LoggerMessage(EventId = 2109, Level = LogLevel.Information, Message = "Peer {NodeId} in {Region} changed from {Previous} to {Current}")]
    public static partial void PeerStatusChanged(ILogger logger, string nodeId, string region, PeerStatus previous, PeerStatus current);

    [LoggerMessage(EventId = 2110, Level = LogLevel.Warning, Message = "Peer {NodeId} host could not be resolved")]
    public static partial void PeerUnresolved(ILogger logger, string nodeId);

    [LoggerMessage(EventId = 2111, Level = LogLevel.Information, Message = "Anti-entropy applied {Applied} of {Fetched} commits for database {DatabaseId}; now at {Bookmark}")]
    public static partial void CatchUpApplied(ILogger logger, Ulid databaseId, int applied, int fetched, CausalBookmark bookmark);

    [LoggerMessage(EventId = 2112, Level = LogLevel.Warning, Message = "Serving catch-up for database {DatabaseId} to {NodeId} skipped an origin: {Reason}")]
    public static partial void CatchUpServeFailed(ILogger logger, Ulid databaseId, string nodeId, string reason);

    [LoggerMessage(EventId = 2113, Level = LogLevel.Error, Message = "Cluster background loop {Loop} failed")]
    public static partial void LoopFailed(ILogger logger, string loop, Exception exception);
}
