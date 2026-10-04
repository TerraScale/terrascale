using TerraScale.TerraBase.Features.Replication.V1;

namespace TerraScale.TerraBase.Features.Replication;

/// <summary>
/// The private incoming journal: a received <see cref="CommittedGroupBatch"/> becomes durable
/// only after <see cref="AppendAsync"/> returns, which is what a <c>DurableAck</c> may report.
/// Not implemented yet.
/// </summary>
public interface IReplicationJournal
{
    /// <summary>Appends one complete committed group and syncs it; returns the new durable prefix of its origin.</summary>
    ValueTask<ulong> AppendAsync(CommittedGroupBatch group, CancellationToken cancellationToken);

    /// <summary>Durable prefix per origin, for the handshake frontiers.</summary>
    ValueTask<IReadOnlyList<PeerFrontier>> GetFrontiersAsync(CancellationToken cancellationToken);
}
