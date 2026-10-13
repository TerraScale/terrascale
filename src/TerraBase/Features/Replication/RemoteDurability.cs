using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>
/// Whether a client write is confirmed beyond this machine's region, and by which regions (decision 0013). A machine of a
/// cell (one with a member directory: Fly's internal DNS, or a multi-machine test's) places every database in at least two
/// regions, and answers a write only once a machine of another placed region has its change group durably. A cell of
/// one (no member directory: development off Fly, single-machine tests) has no second region and answers after its local
/// durable commit.
/// </summary>
public interface IRemoteDurability
{
    /// <summary>Whether this machine's writes need a durable copy in another region: it belongs to a cell.</summary>
    bool Confirms { get; }

    /// <summary>The serving regions other than this machine's, ordered: a new database's default replica regions.</summary>
    ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Admits one write attempt of <paramref name="database"/> before it executes (decision 0013a section 2): refused with
    /// <c>remote_region_unavailable</c> while no machine of another placed region has a connected stream, and with
    /// <c>confirmation_busy</c> past the waiter bounds. Dispose the lease once the attempt is answered.
    /// </summary>
    ValueTask<RemoteAdmission> AdmitAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// After the attempt committed and released its writer: whether an eligible ack of another placed region covers
    /// <paramref name="marker"/> within 10 s. False once the bound passes or no eligible link remains: outcome unknown.
    /// </summary>
    ValueTask<bool> WaitAsync(TenantContext database, CommitMarker marker, CancellationToken cancellationToken);
}

/// <summary>An admitted write attempt, or why it was refused before execution.</summary>
public readonly union RemoteAdmission(RemoteLease, Unavailable);

/// <summary>One admitted write attempt's waiter slot; disposing it releases the slot once.</summary>
public sealed class RemoteLease(Action? release) : IDisposable
{
    private Action? _release = release;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>A cell of one: a write answers after its local durable commit, and a database may be placed in one region.</summary>
internal sealed class LocalDurability : IRemoteDurability
{
    public bool Confirms => false;

    public ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>([]);

    public ValueTask<RemoteAdmission> AdmitAsync(TenantContext database, CancellationToken cancellationToken) =>
        ValueTask.FromResult<RemoteAdmission>(new RemoteLease(null));

    public ValueTask<bool> WaitAsync(TenantContext database, CommitMarker marker, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}
