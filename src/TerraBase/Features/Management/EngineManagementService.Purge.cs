using System.Diagnostics.Metrics;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private string? _purgeAfter;
    private long _purgePending;
    private long _purgeOverdue;
    private long _purgeOldestTicks;
    private long _purgePassPending;
    private long _purgePassOverdue;
    private long _purgePassOldestTicks;
    private readonly Counter<long> _purgeErrors = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.purge.errors");

    private void RegisterPurgeMetrics()
    {
        _recoveryMeter.CreateObservableGauge("terrabase.purge.pending", () => Interlocked.Read(ref _purgePending));
        _recoveryMeter.CreateObservableGauge("terrabase.purge.overdue", () => Interlocked.Read(ref _purgeOverdue));
        _recoveryMeter.CreateObservableGauge("terrabase.purge.oldest_age", () =>
        {
            var ticks = Interlocked.Read(ref _purgeOldestTicks);
            return ticks == 0 ? 0 : Math.Max(0, (time.GetUtcNow() - new DateTimeOffset(ticks, TimeSpan.Zero)).TotalSeconds);
        }, unit: "s");
    }

    private async ValueTask RecordDatabasePurgeAsync(IStorageTransaction transaction, TenantContext storage, ManagedDatabase database, CancellationToken cancellationToken)
    {
        await PutAsync(transaction, ProjectClosure.Purges(storage.ProjectId!), database.Id, storage, TerraBaseJsonContext.Default.TenantContext, cancellationToken).ConfigureAwait(false);
        if (await GetAsync(transaction, DatabasePurge.Collection, database.Id, TerraBaseJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (existing.FormatVersion != 1 || !DatabasePurge.SameDatabase(existing.Database, storage))
            {
                throw new InvalidOperationException("a database purge conflicts with its cleanup scope");
            }

            return;
        }

        var holders = new[] { options.MachineId };
        var requested = time.GetUtcNow();
        var record = new DatabasePurgeRecord(storage, requested, requested.AddDays(30),
            requested.AddDays((options.Backup?.RetentionDays[storage.Plan] ?? 0) + 30), null, [.. holders.Order(StringComparer.Ordinal)]);
        await PutAsync(transaction, DatabasePurge.Collection, database.Id, record, TerraBaseJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ConvergePurgesAsync(CancellationToken cancellationToken)
    {
        var page = await ReadAsync<DocumentPage?>(async transaction => await transaction.ListAsync(DatabasePurge.Collection, _purgeAfter,
            MemberPage, MaxPageBytes, cancellationToken).ConfigureAwait(false), static _ => null, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return;
        }

        foreach (var document in page.Documents)
        {
            try
            {
                var record = EntityPayload.Read(document, TerraBaseJsonContext.Default.DatabasePurgeRecord)
                    ?? throw new InvalidOperationException("a database purge record is invalid");
                if (record.FormatVersion != 1 || record.Database.DatabaseId != document.Id || record.ExpectedHolders.Length is 0 or > 1024)
                {
                    throw new InvalidOperationException("a database purge record has unsupported identity or format");
                }

                var localAbsent = await ReadAsync<bool>(async transaction =>
                    await GetAsync(transaction, DatabasePurge.Receipts(document.Id), options.MachineId, TerraBaseJsonContext.Default.DatabasePurgeReceipt, cancellationToken).ConfigureAwait(false) is { } receipt
                    && receipt.Binds(record, options.MachineId),
                    static _ => false, cancellationToken).ConfigureAwait(false);
                if (!localAbsent)
                {
                    try
                    {
                        await DropLocalAsync(record.Database, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                    {
                        _purgeErrors.Add(1, new KeyValuePair<string, object?>("reason", "active_cleanup"));
                        logger.ZLogWarning(error, $"Applying permanent database purge {document.Id:@Database} failed; the next pass tries again");
                    }
                }

                var absent = await ReadAsync<bool>(transaction => DatabasePurge.AreActiveCopiesAbsentAsync(transaction, record, cancellationToken),
                    static _ => false, cancellationToken).ConfigureAwait(false);
                if (!absent || record.BackupVerifiedAt is null)
                {
                    _purgePassPending++;
                    _purgePassOldestTicks = _purgePassOldestTicks == 0 ? record.RequestedAt.UtcTicks : Math.Min(_purgePassOldestTicks, record.RequestedAt.UtcTicks);
                    var now = time.GetUtcNow();
                    if (!absent && now >= record.ActiveDueAt || record.BackupVerifiedAt is null && now >= record.BackupDueAt)
                    {
                        _purgePassOverdue++;
                    }
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                _purgePassPending++;
                _purgeErrors.Add(1, new KeyValuePair<string, object?>("reason", "record"));
                logger.ZLogWarning(error, $"Applying permanent database purge {document.Id:@Database} failed; the next pass tries again");
            }
        }

        if (page.HasMore)
        {
            _purgeAfter = page.Documents[^1].Id;
            return;
        }

        Interlocked.Exchange(ref _purgePending, _purgePassPending);
        Interlocked.Exchange(ref _purgeOverdue, _purgePassOverdue);
        Interlocked.Exchange(ref _purgeOldestTicks, _purgePassOldestTicks);
        _purgeAfter = null;
        _purgePassPending = _purgePassOverdue = _purgePassOldestTicks = 0;
    }

    private static async ValueTask<bool> HasDeletionAuthorityAsync(IStorageTransaction transaction, TenantContext database, CancellationToken cancellationToken)
    {
        var purge = await GetAsync(transaction, DatabasePurge.Collection, database.DatabaseId!, TerraBaseJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
        return purge is not null
            ? purge.FormatVersion == 1 && DatabasePurge.SameDatabase(purge.Database, database)
            : await transaction.GetVersionAsync(Databases(database.ProjectId!), database.DatabaseId!, cancellationToken).ConfigureAwait(false) is { Deleted: true };
    }

    private async ValueTask<bool> TryRecordLocalPurgeAbsenceAsync(IStorageTransaction transaction, TenantContext database, bool erasureVerified, CancellationToken cancellationToken)
    {
        var purge = await GetAsync(transaction, DatabasePurge.Collection, database.DatabaseId!, TerraBaseJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
        if (purge is null)
        {
            return true;
        }

        if (purge.FormatVersion != 1 || !DatabasePurge.SameDatabase(purge.Database, database))
        {
            throw new InvalidOperationException("a database purge conflicts with its cleanup scope");
        }

        if (!erasureVerified || !webhookErasure.Value.Certifies(database))
        {
            return false;
        }

        await PutAsync(transaction, DatabasePurge.Receipts(database.DatabaseId!), options.MachineId,
            new DatabasePurgeReceipt(database, options.MachineId, time.GetUtcNow(), Authority: purge.RequestedAt), TerraBaseJsonContext.Default.DatabasePurgeReceipt, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
