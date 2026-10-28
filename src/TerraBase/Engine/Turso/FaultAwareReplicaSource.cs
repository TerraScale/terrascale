using System.Runtime.ExceptionServices;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

/// <summary>Refuses source data and acknowledgements from a faulted copy; node faults remain fatal.</summary>
internal sealed class FaultAwareReplicaSource(
    TursoChangeGroupSource source, IReplicaCopyFaults faults, IRebuildStorage storage) : IChangeGroupSource, IReplicaStates, IReplicaSnapshotSource
{
    public async IAsyncEnumerable<ReplicaSnapshotPage> SnapshotAsync(TenantContext database, Ulid incarnation,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var pages = source.SnapshotAsync(database, incarnation, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await ReadAsync(database, incarnation, pages.MoveNextAsync, static () => false, cancellationToken).ConfigureAwait(false))
        {
            yield return pages.Current;
        }
    }

    public IAsyncEnumerable<TenantContext> ListDatabasesAsync(CancellationToken cancellationToken) => source.ListDatabasesAsync(cancellationToken);

    public Task ChangedAsync(CancellationToken cancellationToken) => source.ChangedAsync(cancellationToken);

    public IDisposable Subscribe(Action<TenantContext> changed) => source.Subscribe(changed);

    public ValueTask<SourceGroups?> ReadAsync(
        TenantContext database, Ulid incarnation, long after, int maxGroups, int maxBytes, CancellationToken cancellationToken) =>
        ReadAsync(database, incarnation, () => source.ReadAsync(database, incarnation, after, maxGroups, maxBytes, cancellationToken), static () => null, cancellationToken);

    public ValueTask<SourceCursor?> AckedAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken) =>
        ReadAsync(database, null, async () =>
        {
            var cursor = await source.AckedAsync(database, peer, cancellationToken).ConfigureAwait(false);
            return cursor is { } found && !await faults.RefusedAsync(database, found.Incarnation, cancellationToken).ConfigureAwait(false) ? cursor : null;
        }, static () => (SourceCursor?)null, cancellationToken);

    public ValueTask<bool> AckAsync(TenantContext database, Ulid incarnation, ReplicaId peer, long changeId, Hlc stable, CancellationToken cancellationToken) =>
        ReadAsync(database, incarnation, () => source.AckAsync(database, incarnation, peer, changeId, stable, cancellationToken), static () => false, cancellationToken);

    public ValueTask<bool> ProveAsync(
        TenantContext database, Ulid incarnation, long from, long through, IReadOnlySet<ReplicaId> held, CancellationToken cancellationToken) =>
        ReadAsync(database, incarnation, () => source.ProveAsync(database, incarnation, from, through, held, cancellationToken), static () => false, cancellationToken);

    public ValueTask<bool> RecordStableAsync(TenantContext database, Ulid incarnation, ReplicaId peer, Hlc stable, CancellationToken cancellationToken) =>
        ReadAsync(database, incarnation, () => source.RecordStableAsync(database, incarnation, peer, stable, cancellationToken), static () => false, cancellationToken);

    public ValueTask<bool?> RetainsAsync(TenantContext database, Ulid incarnation, long after, CancellationToken cancellationToken) =>
        ReadAsync(database, incarnation, () => source.RetainsAsync(database, incarnation, after, cancellationToken), static () => null, cancellationToken);

    public ValueTask<long?> HeadAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken) =>
        ReadAsync(database, incarnation, () => source.HeadAsync(database, incarnation, cancellationToken), static () => null, cancellationToken);

    public ValueTask<long?> RetainedAfterAsync(TenantContext database, Ulid incarnation, long? through, long head, CancellationToken cancellationToken) =>
        ReadAsync(database, incarnation, () => source.RetainedAfterAsync(database, incarnation, through, head, cancellationToken), static () => null, cancellationToken);

    public ValueTask<ReplicaDigest?> DigestAsync(TenantContext database, Ulid? expected, CancellationToken cancellationToken) =>
        ReadAsync(database, expected, async () =>
        {
            // Null stays explicit discovery; the returned incarnation comes from A4's digest snapshot.
            var digest = await source.DigestAsync(database, expected, cancellationToken).ConfigureAwait(false);
            return digest is not null && !await faults.RefusedAsync(database, digest.Incarnation, cancellationToken).ConfigureAwait(false) ? digest : null;
        }, static () => (ReplicaDigest?)null, cancellationToken);

    public ValueTask<ReplicaDigest?> MaintainedDigestAsync(TenantContext database, Ulid? expected, CancellationToken cancellationToken) =>
        ReadAsync(database, expected, async () =>
        {
            var digest = await source.MaintainedDigestAsync(database, expected, cancellationToken).ConfigureAwait(false);
            return digest is not null && !await faults.RefusedAsync(database, digest.Incarnation, cancellationToken).ConfigureAwait(false) ? digest : null;
        }, static () => (ReplicaDigest?)null, cancellationToken);

    public ValueTask<bool> MatchedAsync(TenantContext database, ReplicaDigest digest, CancellationToken cancellationToken) =>
        ReadAsync(database, digest.Incarnation, () => source.MatchedAsync(database, digest, cancellationToken), static () => false, cancellationToken);

    public ValueTask<(IReadOnlyList<RepairState> States, BucketCursor? Next)?> ReadBucketsAsync(
        TenantContext database, Ulid incarnation, IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken) =>
        ReadAsync(database, incarnation, () => source.ReadBucketsAsync(database, incarnation, buckets, after, maxStates, maxBytes, cancellationToken), static () => null, cancellationToken);

    public ValueTask<Hlc> StableAsync(TenantContext database, CancellationToken cancellationToken) =>
        ReadAsync(database, null, () => source.StableAsync(database, cancellationToken), RefuseFrontier, cancellationToken);

    public ValueTask<Hlc> HorizonAsync(TenantContext database, CancellationToken cancellationToken) =>
        ReadAsync(database, null, () => source.HorizonAsync(database, cancellationToken), RefuseFrontier, cancellationToken);

    public ValueTask<int?> ForgetAsync(
        TenantContext database, Ulid incarnation, IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, CancellationToken cancellationToken) =>
        ReadAsync(database, incarnation, () => source.ForgetAsync(database, incarnation, keys, horizon, cancellationToken), static () => null, cancellationToken);

    private static Hlc RefuseFrontier() => throw new ReplicaCopyFaultedException("this database copy cannot supply a replication frontier");

    private async ValueTask ThrowIfNodeRefusedAsync(CancellationToken cancellationToken)
    {
        if (await faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, cancellationToken).ConfigureAwait(false))
        {
            throw new ReplicaCopyFaultedException("storage failed closed; this node cannot supply replication data");
        }
    }

    private async ValueTask<T> ReadAsync<T>(
        TenantContext database, Ulid? incarnation, Func<ValueTask<T>> read, Func<T> refused, CancellationToken cancellationToken)
    {
        // An empty SYSTEM pin asks only about node health; A4 still validates real pins in its transactions.
        Ulid? pin = incarnation ?? (database == ChangeGroups.System ? Ulid.Empty : null);
        try
        {
            await ThrowIfNodeRefusedAsync(cancellationToken).ConfigureAwait(false);
            pin ??= await storage.IncarnationAsync(database, cancellationToken).ConfigureAwait(false);
            if (pin is null)
            {
                await ThrowIfNodeRefusedAsync(cancellationToken).ConfigureAwait(false);
                return refused();
            }

            if (await faults.RefusedAsync(database, pin.Value, cancellationToken).ConfigureAwait(false))
            {
                await ThrowIfNodeRefusedAsync(cancellationToken).ConfigureAwait(false);
                return refused();
            }

            var result = await read().ConfigureAwait(false);
            var unavailable = await faults.RefusedAsync(database, pin.Value, cancellationToken).ConfigureAwait(false);
            await ThrowIfNodeRefusedAsync(cancellationToken).ConfigureAwait(false);
            return unavailable ? refused() : result;
        }
        catch (ReplicaCopyFaultedException)
        {
            // Only an established tenant refusal may become null/false. SYSTEM/unrecordable faults end the stream.
            if (database == ChangeGroups.System || await faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, CancellationToken.None).ConfigureAwait(false))
            {
                throw;
            }

            return refused();
        }
        catch (InvalidDataException error) when (pin is not null)
        {
            // The concrete source reads trusted local storage, including unmarked CDC decode failures; never peer bytes.
            try
            {
                await faults.RecordAsync(database, pin.Value, "stored replication data could not be decoded", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The ledger failed closed. Keep the first decoder error, as native storage failures do already.
                ExceptionDispatchInfo.Capture(error).Throw();
                throw;
            }

            throw;
        }
        // Native Turso and other initial failures propagate unchanged; engine disposal records damage/fails closed.
    }
}
