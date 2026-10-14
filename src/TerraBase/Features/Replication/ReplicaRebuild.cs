using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>What a rebuild of a faulted copy came to (docs/terrabase/decisions/0013a, sections 1, 5 and 9).</summary>
internal abstract record RebuildOutcome
{
    private RebuildOutcome()
    {
    }

    /// <summary>The copy was rebuilt and certified as <paramref name="NewIncarnation"/>; the side copy is gone.</summary>
    internal sealed record Completed(Ulid NewIncarnation) : RebuildOutcome;

    /// <summary>Nothing certified; the pending record and the side copy stay, and a later run resumes.</summary>
    internal sealed record Refused(string Reason) : RebuildOutcome;

    /// <summary>Another rebuild runs on this host; nothing changed.</summary>
    internal sealed record Busy : RebuildOutcome;
}

/// <summary>Where a rebuild stands, in the order it gets there.</summary>
internal enum RebuildPhase
{
    /// <summary>Recorded; the faulted file may still be in place.</summary>
    Pending,

    /// <summary>The faulted file and its log are at the side path; a fresh file with a new incarnation is in place.</summary>
    Swapped,

    /// <summary>Refilled from a source, and every verified state of the side copy joined into the new incarnation.</summary>
    Salvaged,

    /// <summary>The completion barrier certified the new incarnation; only the side copy is left to delete.</summary>
    Certified,
}

/// <summary>The one recovery record of a database's rebuild, kept next to its fault record.</summary>
internal sealed record RebuildRecord(
    TenantContext Database, Ulid OldIncarnation, string SidePath, RebuildPhase Phase, Ulid? NewIncarnation, Ulid? AdditionalIncarnation = null);

/// <summary>
/// Where rebuild records persist: outside the suspect file, durable (FULL) before a call returns. Its storage belongs
/// with the node-local copy-fault record in the system database.
/// </summary>
internal interface IRebuildLedger
{
    ValueTask<RebuildRecord?> GetAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask SaveAsync(RebuildRecord record, CancellationToken cancellationToken);

    ValueTask RetireAsync(TenantContext database, Ulid oldIncarnation, CancellationToken cancellationToken);
}

/// <summary>R6-FIX's half of a rebuild: certified refill and the completion barrier (0013a section 1).</summary>
internal interface IRebuildCertification
{
    /// <summary>
    /// Refills exactly <paramref name="newIncarnation"/> from a listed source whose copy is not faulted, with a pinned
    /// source incarnation, complete key-aware coverage and FULL commits; false when it could not.
    /// </summary>
    ValueTask<bool> RefillAsync(TenantContext database, Ulid newIncarnation, CancellationToken cancellationToken);

    /// <summary>
    /// The barrier after the salvage: publishes the certificate for (<paramref name="oldIncarnation"/>,
    /// <paramref name="newIncarnation"/>), retires the fault and leaves the record at <see cref="RebuildPhase.Certified"/>;
    /// false when it did not certify.
    /// </summary>
    ValueTask<bool> CompleteAsync(TenantContext database, Ulid oldIncarnation, Ulid newIncarnation, CancellationToken cancellationToken);
}

/// <summary>The storage half of a rebuild: the swap, the side copy and its verified states.</summary>
internal interface IRebuildStorage
{
    /// <summary>Where the faulted file of <paramref name="incarnation"/> goes aside, next to the database's file.</summary>
    string SidePath(TenantContext database, Ulid incarnation);

    bool SideExists(string sidePath);

    /// <summary>The incarnation of the database's file in place, or null when there is none.</summary>
    ValueTask<Ulid?> IncarnationAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Drains the open copy of <paramref name="faultedIncarnation"/> (no writer of this process mid-transaction, the store
    /// closed), moves its file and log to <paramref name="sidePath"/> unwritten, and publishes a fresh file with a new
    /// incarnation in its place. Null, with the old file untouched in place, when the copy is another incarnation or does
    /// not drain in time.
    /// </summary>
    ValueTask<Ulid?> SwapAsync(TenantContext database, Ulid faultedIncarnation, string sidePath, CancellationToken cancellationToken);

    /// <summary>
    /// After a swap interrupted once its file reached <paramref name="sidePath"/>: moves any log left behind after it, then
    /// publishes the fresh file unless one is in place; returns the incarnation in place.
    /// </summary>
    ValueTask<Ulid?> PublishFreshAsync(TenantContext database, string sidePath, CancellationToken cancellationToken);

    /// <summary>
    /// A private working copy of the side copy (its file and log, copied; the side copy itself is never opened), for the
    /// salvage to read; any older working copy at that path goes first. Returns its path.
    /// </summary>
    ValueTask<string> CopyForSalvageAsync(string sidePath, CancellationToken cancellationToken);

    /// <summary>
    /// The next page of the working copy's states after (<paramref name="afterCollection"/>, <paramref name="afterId"/>), in
    /// that order, measured without reading them: at most <paramref name="maxRows"/>, and <paramref name="maxBytes"/> of
    /// stored states and documents unless the first alone is larger.
    /// </summary>
    ValueTask<IReadOnlyList<SideRow>> MeasureSideAsync(
        string workingPath, string afterCollection, string afterId, int maxRows, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// The states of <paramref name="rows"/> in the working copy, each checked against its stored digest. Throws
    /// <see cref="InvalidDataException"/> for one that does not verify or no longer measures as it did.
    /// </summary>
    ValueTask<IReadOnlyList<RepairState>> ReadSideAsync(string workingPath, IReadOnlyList<SideRow> rows, CancellationToken cancellationToken);

    ValueTask DiscardWorkingCopyAsync(string workingPath, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the side copy, under the engine's creation lock, only while the file in place is exactly
    /// <paramref name="rebuiltIncarnation"/>; false, keeping it, otherwise.
    /// </summary>
    ValueTask<bool> FinalizeAsync(TenantContext database, Ulid rebuiltIncarnation, string sidePath, CancellationToken cancellationToken);
}

/// <summary>A state row of a side copy and the bytes its stored state and document take.</summary>
internal sealed record SideRow(string Collection, string Id, long Bytes);

/// <summary>Rebuilds a faulted copy of a database into a new incarnation; one at a time per host.</summary>
internal interface IReplicaRebuild
{
    ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken);
}

/// <summary>
/// The R7 rebuild of a faulted receiver copy (docs/terrabase/decisions/0013a). A faulted copy acknowledges nothing again;
/// it certifies only as a new incarnation rebuilt from peers, and never at the cost of the only copy of an acknowledged
/// write (section 5). So, in order, each step recorded before the next: the faulted file and its log move aside
/// unwritten and a fresh file takes their place; a certified refill fills it from a source; every state of the side copy
/// that verifies against its stored digest joins it (whatever the source lacks survives); the completion barrier
/// certifies; only then does the side copy go. Any step that cannot finish refuses, keeping the record and the side copy;
/// a later run resumes from the record. The recovery worker resumes durable records at startup and after a copy fault.
/// </summary>
internal sealed class ReplicaRebuild(
    IRebuildStorage storage, IRebuildLedger ledger, IRebuildCertification certification, IRebuildRepair repair, IMemoryBudget memory) : IReplicaRebuild
{
    private const int SalvageRows = ChangeGroups.MaxDeltas;
    private const long SalvageBytes = ChangeGroups.MaxBatchBytes;

    // Stored text, decoded lattice nodes, JSON parser metadata and repair encodings, held through each page's join.
    private const long ReservedPerByte = 16;

    // 1 while a rebuild runs on this host: a second one answers busy, nothing queues in memory.
    private int _active;

    public async ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken)
    {
        if (database == ChangeGroups.System)
        {
            return new RebuildOutcome.Refused("the system database cannot be rebuilt in process");
        }

        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            return new RebuildOutcome.Busy();
        }

        try
        {
            return await RunAsync(database, faultedIncarnation, cancellationToken).ConfigureAwait(false);
        }
        catch (ReplicaCopyFaultedException error)
        {
            return new RebuildOutcome.Refused(error.Message);
        }
        catch (IOException)
        {
            // A file could not move or publish: the record says where to resume, and the side copy (if any) stays.
            return new RebuildOutcome.Refused("the rebuild could not move or publish a file");
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    private async ValueTask<RebuildOutcome> RunAsync(TenantContext database, Ulid faulted, CancellationToken cancellationToken)
    {
        var record = await ledger.GetAsync(database, cancellationToken).ConfigureAwait(false);
        if (record is not null && record.OldIncarnation != faulted)
        {
            return new RebuildOutcome.Refused("a rebuild of another incarnation is pending");
        }

        if (record is null)
        {
            // A fresh request records nothing unless the faulted incarnation is the one in place.
            if (await storage.IncarnationAsync(database, cancellationToken).ConfigureAwait(false) != faulted)
            {
                return new RebuildOutcome.Refused("the faulted incarnation is no longer in place");
            }

            record = new RebuildRecord(database, faulted, storage.SidePath(database, faulted), RebuildPhase.Pending, null);
        }

        if (record.Phase == RebuildPhase.Pending)
        {
            // Recorded before anything moves: a crash from here on resumes from the record.
            await ledger.SaveAsync(record, cancellationToken).ConfigureAwait(false);
            var fresh = storage.SideExists(record.SidePath)
                ? await storage.PublishFreshAsync(database, record.SidePath, cancellationToken).ConfigureAwait(false)
                : await storage.SwapAsync(database, faulted, record.SidePath, cancellationToken).ConfigureAwait(false);
            if (fresh is not { } incarnation || incarnation == faulted)
            {
                return new RebuildOutcome.Refused("the faulted copy is another incarnation or did not drain");
            }

            record = record with { Phase = RebuildPhase.Swapped, NewIncarnation = incarnation };
            await ledger.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        }

        var rebuilt = record.NewIncarnation ?? throw new InvalidDataException("a swapped rebuild record has no new incarnation");
        if (await storage.IncarnationAsync(database, cancellationToken).ConfigureAwait(false) != rebuilt)
        {
            return new RebuildOutcome.Refused("the rebuilt copy was replaced");
        }

        if (record.Phase == RebuildPhase.Swapped)
        {
            if (!await certification.RefillAsync(database, rebuilt, cancellationToken).ConfigureAwait(false))
            {
                return new RebuildOutcome.Refused("no certified refill from an intact source");
            }

            if (await SalvageAsync(database, rebuilt, record.SidePath, cancellationToken).ConfigureAwait(false) is { } failed)
            {
                return new RebuildOutcome.Refused(failed);
            }

            if (record.AdditionalIncarnation is { } additional
                && await SalvageAsync(database, rebuilt, storage.SidePath(database, additional), cancellationToken).ConfigureAwait(false) is { } additionalFailure)
            {
                return new RebuildOutcome.Refused(additionalFailure);
            }

            record = record with { Phase = RebuildPhase.Salvaged };
            await ledger.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        }

        if (record.Phase == RebuildPhase.Salvaged)
        {
            if (!await certification.CompleteAsync(database, faulted, rebuilt, cancellationToken).ConfigureAwait(false)
                || await ledger.GetAsync(database, cancellationToken).ConfigureAwait(false) is not { Phase: RebuildPhase.Certified } certified
                || certified.OldIncarnation != faulted || certified.NewIncarnation != rebuilt)
            {
                return new RebuildOutcome.Refused("the completion barrier did not certify");
            }

            record = certified;
        }

        // Certified: the new incarnation durably contains or dominates the side copy, which may go now, but only while that
        // incarnation is still the file in place.
        if (!await storage.FinalizeAsync(database, rebuilt, record.SidePath, cancellationToken).ConfigureAwait(false))
        {
            return new RebuildOutcome.Refused("the rebuilt copy is no longer in place; the side copy stays");
        }

        if (record.AdditionalIncarnation is { } retained
            && !await storage.FinalizeAsync(database, rebuilt, storage.SidePath(database, retained), cancellationToken).ConfigureAwait(false))
        {
            return new RebuildOutcome.Refused("the rebuilt copy is no longer in place; the additional side copy stays");
        }

        await ledger.RetireAsync(database, faulted, cancellationToken).ConfigureAwait(false);
        return new RebuildOutcome.Completed(rebuilt);
    }

    // Joins every state of the side copy into the new incarnation as repairs, page by page, read from a private working copy
    // and with each page's memory reserved before it is read; the reason it could not, or null.
    private async ValueTask<string?> SalvageAsync(TenantContext database, Ulid rebuilt, string sidePath, CancellationToken cancellationToken)
    {
        var working = await storage.CopyForSalvageAsync(sidePath, cancellationToken).ConfigureAwait(false);
        try
        {
            var (collection, id) = (string.Empty, string.Empty);
            while (true)
            {
                using var metadata = memory.TryReserve(MemoryLane.Bulk, SalvageRows * 8192L, tenant: null);
                if (metadata is null)
                {
                    return "no memory to measure the side copy's next page";
                }

                IReadOnlyList<SideRow> rows;
                try
                {
                    rows = await storage.MeasureSideAsync(working, collection, id, SalvageRows, SalvageBytes, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    return "a key of the side copy exceeds the bounded salvage page";
                }
                if (rows.Count == 0)
                {
                    return null;
                }

                var bytes = rows.Sum(row => row.Bytes);
                if (rows.Count > SalvageRows || bytes > SalvageBytes || rows.Any(row => row.Bytes < 0 || !ChangeGroups.ValidKey(database, row.Collection, row.Id)))
                {
                    return "a state of the side copy exceeds the bounded salvage page";
                }

                using var reservation = memory.TryReserve(MemoryLane.Bulk, bytes * ReservedPerByte, tenant: null);
                if (reservation is null)
                {
                    return "no memory to read the side copy's next page";
                }

                IReadOnlyList<RepairState> page;
                try
                {
                    page = await storage.ReadSideAsync(working, rows, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    return "a state of the side copy does not verify against its digest";
                }

                if (await repair.RepairRebuildAsync(database, rebuilt, page, cancellationToken).ConfigureAwait(false) is null)
                {
                    return "the rebuilt copy is not held";
                }

                (collection, id) = (rows[^1].Collection, rows[^1].Id);
            }
        }
        finally
        {
            await storage.DiscardWorkingCopyAsync(working, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
