using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>Permanent customer deletion authority; deadlines never move when cleanup retries.</summary>
internal sealed record DatabasePurgeRecord(
    TenantContext Database, DateTimeOffset RequestedAt, DateTimeOffset ActiveDueAt, DateTimeOffset BackupDueAt,
    DateTimeOffset? BackupVerifiedAt, string[] ExpectedHolders, int FormatVersion = 1);

/// <summary>
/// A node's own verified absence, kept separately so peers cannot replace each other's receipts. <paramref name="Authority"/>
/// is the <see cref="DatabasePurgeRecord.RequestedAt"/> the node read from the purge it verified, so a receipt binds to that
/// authority causally; <paramref name="VerifiedAt"/> is the holder's own clock, informational only and never compared.
/// </summary>
internal sealed record DatabasePurgeReceipt(TenantContext Database, string MachineId, DateTimeOffset VerifiedAt, int FormatVersion = 1, DateTimeOffset? Authority = null)
{
    /// <summary>Whether this receipt was issued for exactly <paramref name="record"/> by <paramref name="holder"/>.</summary>
    public bool Binds(DatabasePurgeRecord record, string holder) =>
        FormatVersion == 1 && MachineId == holder && DatabasePurge.SameDatabase(Database, record.Database) && Authority == record.RequestedAt;
}

internal static class DatabasePurge
{
    public const string Collection = DatabaseErasure.Collection;

    public static string Receipts(string databaseId) => $"database_purge_receipts/{databaseId}";

    public static async ValueTask<bool> IsDeletedAsync(IStorageTransaction transaction, string databaseId, CancellationToken cancellationToken) =>
        await transaction.GetAsync(Collection, databaseId, cancellationToken).ConfigureAwait(false) is not null;

    public static bool SameDatabase(TenantContext left, TenantContext right) =>
        left.OrganizationId == right.OrganizationId && left.ProjectId == right.ProjectId && left.DatabaseId == right.DatabaseId;

    public static async ValueTask<bool> AreActiveCopiesAbsentAsync(IStorageTransaction transaction, DatabasePurgeRecord record, CancellationToken cancellationToken)
    {
        if (record.FormatVersion != 1 || record.ExpectedHolders.Length is 0 or > 1024)
        {
            return false;
        }

        foreach (var holder in record.ExpectedHolders)
        {
            var receipt = await SystemCatalog.GetAsync(transaction, Receipts(record.Database.DatabaseId!), holder,
                TerraBaseJsonContext.Default.DatabasePurgeReceipt, cancellationToken).ConfigureAwait(false);
            if (receipt is null || !receipt.Binds(record, holder))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The expected holders with no receipt bound to <paramref name="record"/>: whom an erasure waits on.</summary>
    public static async ValueTask<string[]> UnacknowledgedHoldersAsync(IStorageTransaction transaction, DatabasePurgeRecord record, CancellationToken cancellationToken)
    {
        List<string> missing = [];
        foreach (var holder in record.ExpectedHolders.Take(1024))
        {
            var receipt = await SystemCatalog.GetAsync(transaction, Receipts(record.Database.DatabaseId!), holder,
                TerraBaseJsonContext.Default.DatabasePurgeReceipt, cancellationToken).ConfigureAwait(false);
            if (receipt is null || !receipt.Binds(record, holder))
            {
                missing.Add(holder);
            }
        }

        return [.. missing];
    }
}
