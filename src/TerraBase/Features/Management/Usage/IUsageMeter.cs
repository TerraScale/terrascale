using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

/// <summary>Counts document operations per database (by tenant) for usage reporting. Each count also feeds live statistics.</summary>
public interface IUsageMeter
{
    /// <summary>Counts one document read (a get, or a list or query page) of <paramref name="units"/> operation units.</summary>
    void RecordRead(TenantContext database, long units);

    /// <summary>Counts <paramref name="writes"/> committed document writes (puts, patches and deletes) of <paramref name="units"/> operation units.</summary>
    void RecordWrites(TenantContext database, int writes, long units);

    /// <summary>Counts committed operations by type: read, put, patch or delete.</summary>
    void RecordOperation(TenantContext database, string kind, int count);

    /// <summary>Records one completed client request and the body bytes read and written since its last report.</summary>
    void RecordRequest(TenantContext database, string method, int status, TimeSpan elapsed, long received, long sent);

    /// <summary>
    /// Records one SQL statement a protocol (<c>postgres</c> or <c>hrana</c>) ran or refused for the database, separately from
    /// the request that carried it: a Hrana statement error is a failed statement inside a successful HTTP 200 request.
    /// </summary>
    void RecordStatement(TenantContext database, string protocol, bool failed, TimeSpan elapsed);

    /// <summary>Records client body bytes a request still in progress read and wrote, in the minute it transferred them.</summary>
    void RecordClientBytes(TenantContext database, long received, long sent, DateTimeOffset? transferredAt = null);

    /// <summary>Has <paramref name="report"/> run on each flush tick until the returned registration is disposed.</summary>
    IDisposable TrackActive(Action report);

    /// <summary>Counts encoded replication message bytes on each transport attempt, separately from client traffic.</summary>
    void RecordReplicationBytes(TenantContext database, string peerRegion, string kind, bool sent, long bytes);

    /// <summary>Counts operations only when a remote change group is newly applied.</summary>
    void RecordReplicatedOperations(TenantContext database, string peerRegion, int operations);

    /// <summary>Records replication backlog and divergence observations.</summary>
    void RecordReplicationState(TenantContext database, string peerRegion, long remaining, bool diverged, double? convergenceMs = null);
}

/// <summary>
/// The billing unit of https://terrascale.tech/pricing.json (<c>metering.operationUnit</c>): each started 4 KiB of a
/// logical object successfully read or written. Replicated applies count none.
/// </summary>
public static class OperationUnits
{
    /// <summary>Bytes per unit.</summary>
    public const int Bytes = 4096;

    /// <summary>The units of an object of <paramref name="bytes"/>: none for none.</summary>
    public static long Of(long bytes) => (bytes + Bytes - 1) / Bytes;
}
