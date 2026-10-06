using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Replication;

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
    ValueTask<(IDatabaseStore Store, ReadOnlyMemory<byte> Receiver, long Applied, ReadOnlyMemory<byte> Digest)> OpenAsync(TenantContext database, ReadOnlyMemory<byte> origin, CancellationToken cancellationToken);

    /// <summary>
    /// Applies change group <paramref name="sequence"/> of <paramref name="origin"/> when it is the next one,
    /// on the pinned <paramref name="store"/>, and returns the applied prefix afterwards. A retired store
    /// fails instead of reopening the database by tenant identity. A group at or below the
    /// prefix is a duplicate and changes nothing; a prefix below <paramref name="sequence"/> means a gap.
    /// </summary>
    ValueTask<(long Sequence, ReadOnlyMemory<byte> Digest)> AppendAsync(
        IDatabaseStore store, ReadOnlyMemory<byte> origin, long sequence, IReadOnlyList<DocumentChange> changes,
        ReadOnlyMemory<byte> previousDigest, ReadOnlyMemory<byte> digest, CancellationToken cancellationToken);
}

internal sealed class EngineReplicationJournal(IStorageEngine engine) : IReplicationJournal
{
    public async ValueTask<(IDatabaseStore Store, ReadOnlyMemory<byte> Receiver, long Applied, ReadOnlyMemory<byte> Digest)> OpenAsync(
        TenantContext database, ReadOnlyMemory<byte> origin, CancellationToken cancellationToken)
    {
        var store = await OpenOrCreateAsync(database, cancellationToken).ConfigureAwait(false);
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var (sequence, digest) = await transaction.GetAppliedFrontierAsync(origin, cancellationToken).ConfigureAwait(false);
            return (store, await transaction.GetOriginAsync(cancellationToken).ConfigureAwait(false), sequence, digest);
        }
    }

    public async ValueTask<(long Sequence, ReadOnlyMemory<byte> Digest)> AppendAsync(
        IDatabaseStore store, ReadOnlyMemory<byte> origin, long sequence, IReadOnlyList<DocumentChange> changes,
        ReadOnlyMemory<byte> previousDigest, ReadOnlyMemory<byte> digest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ObjectDisposedException.ThrowIf(store.IsClosed, store);

        // The applied prefix is read inside the write transaction, which the engine serializes per database,
        // so concurrent streams of one origin cannot both apply a group. A DurableAck reports the group: its commit is
        // synced to disk before it returns (docs/decisions/0010).
        var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var applied = await transaction.GetAppliedFrontierAsync(origin, cancellationToken).ConfigureAwait(false);
            if (digest.Length != JournalDigest.Length || previousDigest.Length != JournalDigest.Length
                || !digest.Span.SequenceEqual(JournalDigest.Compute(origin.Span, sequence, previousDigest.Span, changes)))
            {
                throw new InvalidDataException("the group journal proof is invalid");
            }

            if (sequence != applied.Sequence + 1)
            {
                if (sequence == applied.Sequence && !digest.Span.SequenceEqual(applied.Digest.Span))
                {
                    throw new InvalidDataException("the duplicate group has a different journal identity");
                }

                return applied;
            }

            if (!previousDigest.Span.SequenceEqual(applied.Digest.Span))
            {
                throw new InvalidDataException("the group does not extend the applied journal prefix");
            }

            // Validate the complete group against one starting head before any document, collection,
            // clock or applied-frontier mutation. Apply also guards direct engine callers.
            var startingHead = await transaction.GetOffsetAsync(cancellationToken).ConfigureAwait(false);
            foreach (var change in changes)
            {
                VersionClock.ValidateImport(change.Version, startingHead);
            }

            foreach (var change in changes)
            {
                await transaction.ApplyAsync(change, cancellationToken).ConfigureAwait(false);
            }

            await transaction.SetAppliedFrontierAsync(origin, sequence, digest, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(store.IsClosed, store);
            return (sequence, digest);
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
