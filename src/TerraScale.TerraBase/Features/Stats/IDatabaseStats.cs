using TerraBase.Tenancy;

namespace TerraBase.Features.Stats;

/// <summary>Live per-database statistics. Calls are lock-free and never fail; refused events only count as dropped.</summary>
public interface IDatabaseStats
{
    /// <summary>Counts one document read of a database.</summary>
    void Read(TenantContext database);

    /// <summary>Counts committed document writes of a database.</summary>
    void Wrote(TenantContext database, int writes);

    /// <summary>Counts one completed request of a database; <paramref name="failed"/> marks an error response.</summary>
    void Request(TenantContext database, TimeSpan elapsed, bool failed);

    /// <summary>Counts replication bytes a database sent to or received from a peer region.</summary>
    void ReplicationBytes(TenantContext database, string peerRegion, bool sent, long bytes);

    /// <summary>Records the latest state with a peer region: change groups outstanding, lag in milliseconds and divergence.</summary>
    void ReplicationState(TenantContext database, string peerRegion, long remaining, double lagMs, bool diverged);

    /// <summary>Records the size, in bytes, of a database's replica on this member.</summary>
    void StorageSampled(TenantContext database, long bytes);

    /// <summary>Counts one document read of a collection of a database.</summary>
    void CollectionRead(TenantContext database, string collection);

    /// <summary>Counts one committed document write of a collection of a database.</summary>
    void CollectionWrote(TenantContext database, string collection);
}
