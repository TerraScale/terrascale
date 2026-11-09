using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed record DatabasePurgeRecord(
    TenantContext Database, DateTimeOffset RequestedAt, DateTimeOffset ActiveDueAt, DateTimeOffset BackupDueAt,
    DateTimeOffset? BackupVerifiedAt, string[] ExpectedHolders, int FormatVersion = 1);

internal sealed record DatabasePurgeReceipt(TenantContext Database, string MachineId, DateTimeOffset VerifiedAt, int FormatVersion = 1, DateTimeOffset? Authority = null)
{
    public bool Binds(DatabasePurgeRecord record, string holder) =>
        FormatVersion == 1 && MachineId == holder && DatabasePurge.SameDatabase(Database, record.Database) && Authority == record.RequestedAt;
}

internal static class DatabasePurge
{
    public const string Kind = DatabaseErasure.Kind;

    public static string Receipts(string databaseId) => $"database_purge_receipts/{databaseId}";

    public static async ValueTask<bool> IsDeletedAsync(IStorageTransaction transaction, string databaseId, CancellationToken cancellationToken) =>
        await transaction.GetAsync(Kind, databaseId, cancellationToken).ConfigureAwait(false) is not null;

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
