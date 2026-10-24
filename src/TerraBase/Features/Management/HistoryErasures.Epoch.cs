using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

internal sealed partial class HistoryErasures
{
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
            .Where(peer => receipts.Any(receipt => receipt.MachineId == peer.Name)).OrderBy(peer => peer.Name, StringComparer.Ordinal).ToList();
        var peer = peers.FirstOrDefault(peer => own.RecoveryAfter is null || string.CompareOrdinal(peer.Name, own.RecoveryAfter) > 0)
            ?? peers.FirstOrDefault();
        if (peer is null)
        {
            return;
        }

        try
        {
            var snapshots = services.GetRequiredService<ReplicaSnapshots>();
            if (!await snapshots.PullAsync(AuthDatabase, peer, origins, cancellationToken).ConfigureAwait(false)
                || !await snapshots.PullAsync(ChangeGroups.System, peer, origins, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // Imported origin cuts came from the same read snapshot as every joined state, not from a new epoch or a root
            // comparison. Re-fence, then repeat all-holder settlement and scoped auth re-verification before a final cut.
            await MarkAsync(record, own, cancellationToken, peer.Name).ConfigureAwait(false);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
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
}
