using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

/// <summary>Counts document operations per database (by tenant) for usage reporting.</summary>
public interface IUsageMeter
{
    /// <summary>Counts one document read (a get, or a list or query page).</summary>
    void RecordRead(TenantContext database);

    /// <summary>Counts <paramref name="writes"/> committed document writes (puts, patches and deletes).</summary>
    void RecordWrites(TenantContext database, int writes);

    /// <summary>Counts committed operations by type: read, put, patch or delete.</summary>
    void RecordOperation(TenantContext database, string kind, int count);

    /// <summary>Records one completed client request and the body bytes read and written since its last report.</summary>
    void RecordRequest(TenantContext database, string method, int status, TimeSpan elapsed, long received, long sent);

    /// <summary>Records client body bytes a request still in progress read and wrote since its last report.</summary>
    void RecordClientBytes(TenantContext database, long received, long sent);

    /// <summary>Counts encoded replication message bytes on each transport attempt, separately from client traffic.</summary>
    void RecordReplicationBytes(TenantContext database, string peerRegion, string kind, bool sent, long bytes);

    /// <summary>Counts operations only when a remote change group is newly applied.</summary>
    void RecordReplicatedOperations(TenantContext database, string peerRegion, int operations);

    /// <summary>Records replication backlog and divergence observations.</summary>
    void RecordReplicationState(TenantContext database, string peerRegion, long remaining, bool diverged, double? convergenceMs = null);
}
