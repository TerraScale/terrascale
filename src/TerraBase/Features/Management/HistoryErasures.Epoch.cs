using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

internal sealed partial class HistoryErasures
{
    private readonly Dictionary<(string Erasure, string Peer), (int Evidence, DateTimeOffset Next, TimeSpan Delay)> _recovery = [];

    // Per peer, when its snapshots may be pulled again after a pull that moved no erasure on. A pull copies the peer's whole
    // auth and SYSTEM files, the same for every erasure, and holds SYSTEM's writer for the import: keyed per erasure, 150
    // stalled erasures pulled back to back and held it about 60% of the time (gru, 2026-10-28), so creates timed out.
    private readonly Dictionary<string, (DateTimeOffset Next, TimeSpan Delay)> _peerPulls = [with(StringComparer.Ordinal)];

    private static ulong Cut(IEnumerable<OriginCut> cuts, ulong replica) =>
        cuts.Where(cut => cut.Replica == replica).Select(cut => cut.Stamp).DefaultIfEmpty().Max();

    private static OriginCut[] MergeCuts(IEnumerable<OriginCut> cuts, OriginCut additional) =>
        [.. cuts.Append(additional).GroupBy(cut => cut.Replica).Select(group => new OriginCut(group.Key, group.Max(cut => cut.Stamp))).OrderBy(cut => cut.Replica)];

    private static HashSet<ulong> ObsoleteOrigins(IReadOnlyList<HistoryErasureReceipt> receipts)
    {
        var reported = receipts.SelectMany(receipt => receipt.AuthApplied.Concat(receipt.SystemApplied)).Select(cut => cut.Replica);
        return [.. receipts.SelectMany(receipt => receipt.PreviousReplicas ?? []).Concat(reported.Where(origin => receipts.Any(receipt =>
            (receipt.Replica & ~(ulong)ushort.MaxValue) == (origin & ~(ulong)ushort.MaxValue) && receipt.Replica > origin)))];
    }

    private static ulong[] EpochView(IReadOnlyList<HistoryErasureReceipt> receipts) => [.. receipts.Select(receipt => receipt.Replica).Order()];

    private static bool NeedsEpochFence(HistoryErasureReceipt own, IReadOnlyList<HistoryErasureReceipt> receipts) =>
        ObsoleteOrigins(receipts).Count > 0 && (own.RecutReplicas is null || !own.RecutReplicas.SequenceEqual(EpochView(receipts)));

    private static bool NeedsOldCoverage(HistoryErasureReceipt own, IReadOnlyList<HistoryErasureReceipt> receipts, IReadOnlySet<ulong> origins) =>
        own.PreviousReplicas is { Length: > 0 } || origins.Any(origin =>
            Cut(own.AuthApplied, origin) < receipts.Max(receipt => Cut(receipt.AuthApplied, origin))
            || Cut(own.SystemApplied, origin) < receipts.Max(receipt => Cut(receipt.SystemApplied, origin)));

    private async Task RecoverEpochAsync(HistoryErasureRecord record, HistoryErasureReceipt own,
        IReadOnlyList<HistoryErasureReceipt> receipts, CancellationToken cancellationToken)
    {
        var origins = ObsoleteOrigins(receipts);
        if (!NeedsOldCoverage(own, receipts, origins) || services.GetService<IMembership>() is not { } membership)
        {
            return;
        }

        // One peer's two files per pass, bounded by the snapshot RPC deadline. The receipt's cursor survives a restart.
        var peers = AntiEntropy.Peers(await membership.RefreshAsync(cancellationToken).ConfigureAwait(false), Machine)
            .Where(peer => receipts.Any(receipt => receipt.MachineId == peer.Name)
                && (!_recovery.TryGetValue((record.ErasureId, peer.Name), out var wait)
                    || wait.Evidence != RecoveryEvidence(own, receipts.Single(receipt => receipt.MachineId == peer.Name), origins)
                    || time.GetUtcNow() >= wait.Next)).OrderBy(peer => peer.Name, StringComparer.Ordinal).ToList();
        var peer = peers.FirstOrDefault(peer => own.RecoveryAfter is null || string.CompareOrdinal(peer.Name, own.RecoveryAfter) > 0)
            ?? peers.FirstOrDefault();
        if (peer is null)
        {
            return;
        }

        var key = (record.ErasureId, peer.Name);
        // Another erasure's pull may already have imported what this one needs: the local cuts are read before pulling.
        if (await ProgressedAsync(own, origins, cancellationToken).ConfigureAwait(false))
        {
            await MarkAsync(record, own, cancellationToken, peer.Name).ConfigureAwait(false);
            _recovery.Remove(key);
            return;
        }

        if (_peerPulls.TryGetValue(peer.Name, out var pull) && time.GetUtcNow() < pull.Next)
        {
            return;
        }

        var evidence = RecoveryEvidence(own, receipts.Single(receipt => receipt.MachineId == peer.Name), origins);
        var progressed = false;
        var completed = false;
        try
        {
            var snapshots = services.GetRequiredService<ReplicaSnapshots>();
            if (!await snapshots.PullAsync(AuthDatabase, peer, origins, cancellationToken).ConfigureAwait(false)
                || !await snapshots.PullAsync(ChangeGroups.System, peer, origins, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            completed = true;
            progressed = await ProgressedAsync(own, origins, cancellationToken).ConfigureAwait(false);
            if (progressed)
            {
                // Only actual imported prefix progress warrants a fresh fence. No-op imports must not churn every
                // holder's settlement digest, or imply that an old marker nobody retained has been proved.
                await MarkAsync(record, own, cancellationToken, peer.Name).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                if (completed)
                {
                    // A pull that moved this erasure on may move others too: their next pass looks without pulling.
                    var wait = progressed ? TimeSpan.Zero
                        : _peerPulls.TryGetValue(peer.Name, out var last) && last.Delay > TimeSpan.Zero
                            ? TimeSpan.FromTicks(Math.Min(last.Delay.Ticks * 2, MaxBackoff.Ticks)) : TimeSpan.FromMinutes(1);
                    _peerPulls[peer.Name] = (time.GetUtcNow() + wait, wait);
                }

                if (progressed)
                {
                    _recovery.Remove(key);
                }
                else
                {
                    var delay = _recovery.TryGetValue(key, out var previous) && previous.Evidence == evidence
                        ? TimeSpan.FromTicks(Math.Min(previous.Delay.Ticks * 2, MaxBackoff.Ticks)) : completed ? TimeSpan.FromMinutes(1) : Interval;
                    // A bounded scheduling cache, never coverage proof. Eviction/restart permits an extra attempt.
                    if (_recovery.Count == HistoryErasure.MaxHolders)
                    {
                        _recovery.Remove(_recovery.MinBy(entry => entry.Value.Next).Key);
                    }

                    _recovery[key] = (evidence, time.GetUtcNow() + delay, delay);
                    _errors.Add(1, new KeyValuePair<string, object?>("reason", "epoch_no_progress"));
                }

                // A failed/offline first witness must not starve a later one. Persist only the cursor, never coverage.
                _ = await catalog.WriteDurableAsync(async transaction =>
                {
                    var current = await SystemCatalog.GetAsync(transaction, HistoryErasure.Receipts(record.ErasureId), Machine,
                        TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken).ConfigureAwait(false);
                    if (current is null || current.Replica != Self)
                    {
                        return false;
                    }

                    await SystemCatalog.PutAsync(transaction, HistoryErasure.Receipts(record.ErasureId), Machine,
                        current with { RecoveryAfter = peer.Name }, TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken).ConfigureAwait(false);
                    return true;
                }, static _ => false, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Whether this machine's auth or SYSTEM file now holds more of an obsolete origin than its receipt recorded.
    private async Task<bool> ProgressedAsync(HistoryErasureReceipt own, IReadOnlySet<ulong> origins, CancellationToken cancellationToken)
    {
        var auth = await engine.OpenDatabaseAsync(AuthDatabase, cancellationToken).ConfigureAwait(false);
        if (auth is null)
        {
            return false;
        }

        await using var read = await auth.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var authCuts = await read.GetAppliedAsync(cancellationToken).ConfigureAwait(false);
        var systemCuts = await catalog.ReadUncheckedAsync(transaction => transaction.GetAppliedAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        return origins.Any(origin => authCuts.GetValueOrDefault(new(origin)).Value > Cut(own.AuthApplied, origin)
            || systemCuts.GetValueOrDefault(new(origin)).Value > Cut(own.SystemApplied, origin));
    }

    // Scheduling evidence excludes the ever-changing fence marker and cursor. A hash collision can delay a retry at
    // most an hour; it can never certify coverage. A peer's advancing old cuts or a new epoch retries immediately.
    private static int RecoveryEvidence(HistoryErasureReceipt own, HistoryErasureReceipt peer, IReadOnlySet<ulong> origins)
    {
        var hash = new HashCode();
        hash.Add(own.Replica);
        hash.Add(peer.Replica);
        foreach (var origin in origins.Order())
        {
            hash.Add(origin);
            hash.Add(Cut(own.PreviousAuth ?? [], origin));
            hash.Add(Cut(own.PreviousSystem ?? [], origin));
            hash.Add(Cut(peer.AuthApplied, origin));
            hash.Add(Cut(peer.SystemApplied, origin));
        }

        return hash.ToHashCode();
    }
}
