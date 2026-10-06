using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Replication;

/// <summary>Write-wait (docs/decisions/0010): read-your-writes on every member that a client write may be read from next.</summary>
public interface IReplicationWait
{
    /// <summary>
    /// Waits, at most <c>write-wait-ms</c>, until every active peer durably acknowledged the change groups that
    /// <paramref name="database"/> had committed when it was called. Call it after a write transaction ended, never inside
    /// one. It never fails the write: on expiry it returns and counts a timeout.
    /// </summary>
    ValueTask WaitAsync(TenantContext database, CancellationToken cancellationToken);
}

/// <summary>A node that sends to no peer has nothing to wait for.</summary>
internal sealed class NoReplicationWait : IReplicationWait
{
    public ValueTask WaitAsync(TenantContext database, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
