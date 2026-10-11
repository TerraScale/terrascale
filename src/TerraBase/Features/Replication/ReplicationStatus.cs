namespace TerraBase.Features.Replication;

/// <summary>Local outbound replication observations, available only to operators.</summary>
public interface IReplicationStatus
{
    /// <summary>Returns a bounded snapshot; omitted databases remain included in aggregate lag.</summary>
    ReplicationStatus Snapshot();
}

/// <summary>Outbound status. Zero lag is meaningful only when initialized. Detail covers at most 128 database incarnations.</summary>
public sealed record ReplicationStatus(bool Enabled, bool Initialized, int DatabaseCount, int OmittedDatabases, IReadOnlyList<ReplicationPeerStatus> Peers);

/// <summary>A configured peer's position in peer-addresses, total durable lag and bounded database detail.</summary>
public sealed record ReplicationPeerStatus(int Peer, long DurableLag, IReadOnlyList<ReplicationDatabaseStatus> Databases, string Region = "");

/// <summary>
/// Slot identifies a bounded metric label; identities appear only in this loopback response. Diverged means a detected
/// journal conflict, not a full document comparison. CatchupSeconds is the current backlog age; ConvergenceSeconds is
/// the last completed catch-up, or null until one has completed. Observations reset on process restart and slot reuse.
/// </summary>
public sealed record ReplicationDatabaseStatus(
    int Slot, string OrganizationId, string ProjectId, string DatabaseId, Ulid Incarnation,
    long Head, long Acknowledged, long DurableLag, bool Diverged, double CatchupSeconds, double? ConvergenceSeconds);

internal sealed class NoReplicationStatus : IReplicationStatus
{
    public ReplicationStatus Snapshot() => new(false, true, 0, 0, []);
}
