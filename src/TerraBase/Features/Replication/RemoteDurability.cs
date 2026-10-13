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
}

/// <summary>A cell of one: a write answers after its local durable commit, and a database may be placed in one region.</summary>
internal sealed class LocalDurability : IRemoteDurability
{
    public bool Confirms => false;

    public ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>([]);
}
