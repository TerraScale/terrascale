using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>
/// The incoming journal: a received change group becomes durable (synced to disk) and visible in one local transaction
/// that also records it as applied, and only then may a <c>DurableAck</c> or <c>AppliedAck</c> report it.
/// </summary>
public interface IReplicationJournal
{
    /// <summary>
    /// Opens <paramref name="database"/>, creating it when missing, and returns the pinned store and its own origin (its identity
    /// as a receiver) and its applied prefix of <paramref name="origin"/>, 0 when none.
    /// </summary>
    ValueTask<(IDatabaseStore Store, ReadOnlyMemory<byte> Receiver, long Applied)> OpenAsync(TenantContext database, ReadOnlyMemory<byte> origin, CancellationToken cancellationToken);

    /// <summary>
    /// Applies change group <paramref name="sequence"/> of <paramref name="origin"/> when it is the next one,
    /// on the pinned <paramref name="store"/>, and returns the applied prefix afterwards. A retired store
    /// fails instead of reopening the database by tenant identity. A group at or below the
    /// prefix is a duplicate and changes nothing; a prefix below <paramref name="sequence"/> means a gap.
    /// Only a call that committed a new group returns <see cref="JournalApplied"/>, also with concurrent streams.
    /// An origin never reuses a sequence, so a sequence alone identifies a group.
    /// </summary>
    ValueTask<JournalAppend> AppendAsync(IDatabaseStore store, ReadOnlyMemory<byte> origin, long sequence, CapturedGroup group, CancellationToken cancellationToken);
}

internal sealed class EngineReplicationJournal(IStorageEngine engine) : IReplicationJournal
{
    public async ValueTask<(IDatabaseStore Store, ReadOnlyMemory<byte> Receiver, long Applied)> OpenAsync(
        TenantContext database, ReadOnlyMemory<byte> origin, CancellationToken cancellationToken)
    {
        var store = await OpenOrCreateAsync(database, cancellationToken).ConfigureAwait(false);
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var applied = await transaction.GetAppliedSequenceAsync(origin, cancellationToken).ConfigureAwait(false);
            return (store, await transaction.GetOriginAsync(cancellationToken).ConfigureAwait(false), applied);
        }
    }

    public async ValueTask<JournalAppend> AppendAsync(
        IDatabaseStore store, ReadOnlyMemory<byte> origin, long sequence, CapturedGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(group);
        ObjectDisposedException.ThrowIf(store.IsClosed, store);

        // The applied prefix is read inside the write transaction, which the engine serializes per database,
        // so concurrent streams of one origin cannot both apply a group. A DurableAck reports the group: its commit is
        // synced to disk before it returns (docs/decisions/0010).
        var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var applied = await transaction.GetAppliedSequenceAsync(origin, cancellationToken).ConfigureAwait(false);
            if (sequence != applied + 1)
            {
                // A duplicate, or a gap the sender closes from the prefix on its next stream.
                return new JournalUnchanged(applied);
            }

            foreach (var change in group.Changes)
            {
                await transaction.ApplyAsync(change, cancellationToken).ConfigureAwait(false);
            }

            await transaction.SetAppliedSequenceAsync(origin, sequence, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(store.IsClosed, store);
            return new JournalApplied(sequence);
        }
    }

    private async ValueTask<IDatabaseStore> OpenOrCreateAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is { } store)
        {
            return store;
        }

        await engine.CreateDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        return await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the database was deleted while a peer opened it");
    }
}

/// <summary>The applied prefix after this call committed a new group.</summary>
public sealed record JournalApplied(long Sequence);

/// <summary>The unchanged applied prefix: the group was a duplicate, or it left a gap.</summary>
public sealed record JournalUnchanged(long Sequence);

/// <summary>Whether a group committed or left the prefix unchanged, unlike a malformed or oversized message.</summary>
public readonly union JournalAppend(JournalApplied, JournalUnchanged);
