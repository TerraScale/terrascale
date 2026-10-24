using TerraBase.Errors;

namespace TerraBase.Features.Management;

internal readonly union HistoryErasureInspectionResult(HistoryErasureInspection, NotFound, Unavailable);

internal sealed partial class HistoryErasures
{
    // One guarded read transaction. In particular, do not call RequestAsync: even a tokenless request can settle.
    internal async Task<HistoryErasureInspectionResult> InspectAsync(string erasureId, CancellationToken cancellationToken) =>
        await catalog.ReadUncheckedAsync<HistoryErasureInspectionResult>(async transaction =>
        {
            // Unlike ReadAsync, this must not initialize a fresh management catalog as a side effect of a GET.
            if (await services.GetRequiredService<ICatalogGuard>().CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false) is Unavailable refused)
            {
                return refused;
            }

            var record = await GetRecordAsync(transaction, erasureId, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return new NotFound("the history erasure was not found");
            }

            var holders = await HoldersAsync(transaction, record, cancellationToken).ConfigureAwait(false);
            var receipts = await ReceiptsAsync(transaction, record, cancellationToken).ConfigureAwait(false);
            var status = Compute(record, holders, receipts);
            return new HistoryErasureInspection(record.ErasureId, record.RequestedAt, status.Status, status.PendingHolders,
                record.ActiveDueAt, record.BackupDueAt, record.Requester, [.. holders.Order(StringComparer.Ordinal)],
                record.FinalId, record.FinalAuth, record.FinalSystem, record.BackupCleanAt, record.BackupFinalId, record.BackupVerifiedAt,
                record.CertifiedHolders, [.. receipts.Select(receipt => new HistoryHolderInspection(receipt.MachineId, receipt.Replica,
                    receipt.Phase, receipt.FinalId, receipt.MarkedAt, receipt.Auth, receipt.System, receipt.Residue, receipt.Retired,
                    receipt.PreviousReplicas, receipt.RewrittenAt, receipt.ResidueAt, receipt.RetiredAt))]);
        }, cancellationToken).ConfigureAwait(false);
}

internal sealed record HistoryErasureInspection(string ErasureId, DateTimeOffset RequestedAt, string Status, int PendingHolders,
    DateTimeOffset ActiveDueAt, DateTimeOffset BackupDueAt, string? Requester, string[] Holders,
    string? FinalId, OriginCut[]? FinalAuth, OriginCut[]? FinalSystem, DateTimeOffset? BackupCleanAt, string? BackupFinalId,
    DateTimeOffset? BackupVerifiedAt, string[]? CertifiedHolders, HistoryHolderInspection[] Receipts);

internal sealed record HistoryHolderInspection(string MachineId, ulong Replica, string Phase, string? FinalId, DateTimeOffset MarkedAt,
    HistoryRewrite? Auth, HistoryRewrite? System, HistoryRewrite? Residue, HistoryRewrite? Retired, ulong[]? PreviousReplicas,
    DateTimeOffset? RewrittenAt, DateTimeOffset? ResidueAt, DateTimeOffset? RetiredAt);
