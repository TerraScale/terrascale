using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Features.Replicas;
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
    private readonly ConcurrentDictionary<(string Database, DateTimeOffset RequestedAt), byte> _skippedPurges = new();
    private readonly ConcurrentDictionary<(string Database, DateTimeOffset RequestedAt), string> _deliveredReceipts = new();
    private readonly ConcurrentDictionary<(string Database, DateTimeOffset RequestedAt), string> _receiptWaits = new();
    private long _receiptsUndelivered;
    private long _receiptsPassUndelivered;

    private void RegisterPurgeMetrics()
    {
        _recoveryMeter.CreateObservableGauge("terrabase.purge.pending", () => Interlocked.Read(ref _purgePending));
        _recoveryMeter.CreateObservableGauge("terrabase.purge.overdue", () => Interlocked.Read(ref _purgeOverdue));
        _recoveryMeter.CreateObservableGauge("terrabase.purge.oldest_age", () =>
        {
            var ticks = Interlocked.Read(ref _purgeOldestTicks);
            return ticks == 0 ? 0 : Math.Max(0, (time.GetUtcNow() - new DateTimeOffset(ticks, TimeSpan.Zero)).TotalSeconds);
        }, unit: "s");
        _recoveryMeter.CreateObservableGauge("terrabase.purge.receipts_undelivered", () => Interlocked.Read(ref _receiptsUndelivered),
            description: "Erasure receipts this node recorded for its own erased copies while it is not its region's writer, that no regional writer has recorded yet.");
    }

    private async ValueTask RecordDatabasePurgeAsync(IStorageTransaction transaction, TenantContext storage, ManagedDatabase database, CancellationToken cancellationToken)
    {
        await PutAsync(transaction, ProjectClosure.Purges(storage.ProjectId!), database.Id, storage, TerraBaseJsonContext.Default.TenantContext, cancellationToken).ConfigureAwait(false);
        if (await GetAsync(transaction, DatabasePurge.Kind, database.Id, ManagementJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false) is { } existing)
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
        await PutAsync(transaction, DatabasePurge.Kind, database.Id, record, ManagementJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ObserveShreddedResult> ObserveShreddedAsync(TenantContext database, DateTimeOffset deletedAt, CancellationToken token)
    {
        if (ShredGuard.Refusal(database) is { } refusal)
        {
            return refusal;
        }

        return await WriteAsync<bool>(async transaction =>
        {
            var known = await GetAsync(transaction, DatabasePurge.Kind, database.DatabaseId!, ManagementJsonContext.Default.DatabasePurgeRecord, token).ConfigureAwait(false);
            if (known is not null && (known.FormatVersion != 1 || !DatabasePurge.SameDatabase(known.Database, database)))
            {
                return false;
            }
            if (await GetAsync(transaction, DeletedDatabases.Kind, database.DatabaseId!, ManagementJsonContext.Default.DeletedDatabaseRecord, token).ConfigureAwait(false) is { } retained)
            {
                if (!DatabasePurge.SameDatabase(retained.Storage, database))
                {
                    return false;
                }
                await DeletedDatabases.RemoveAsync(transaction, retained, token).ConfigureAwait(false);
            }
            if (known is not null)
            {
                return true;
            }
            if (await CatalogTables.DatabaseAsync(transaction, database.ProjectId!, database.DatabaseId!, token).ConfigureAwait(false) is { } registered)
            {
                _ = await UnregisterRecordAsync(transaction, database, registered, token, customerDeletion: true).ConfigureAwait(false);
            }
            else
            {
                var record = new DatabasePurgeRecord(database, deletedAt, deletedAt.AddDays(30), deletedAt.AddDays(60), null, [options.MachineId]);
                await PutAsync(transaction, DatabasePurge.Kind, database.DatabaseId!, record, ManagementJsonContext.Default.DatabasePurgeRecord, token).ConfigureAwait(false);
            }
            return true;
        }, static _ => false, token).ConfigureAwait(false);
    }

    public async Task<bool> EraseShreddedAsync(TenantContext database, CancellationToken token)
    {
        var certified = await ReadAsync<bool>(async transaction =>
            await GetAsync(transaction, DatabasePurge.Kind, database.DatabaseId!, ManagementJsonContext.Default.DatabasePurgeRecord, token).ConfigureAwait(false) is { } purge
            && await GetAsync(transaction, DatabasePurge.Receipts(database.DatabaseId!), options.MachineId,
                ManagementJsonContext.Default.DatabasePurgeReceipt, token).ConfigureAwait(false) is { } receipt
            && receipt.Binds(purge, options.MachineId), static _ => false, token).ConfigureAwait(false);
        if (!certified)
        {
            await DropLocalAsync(database, token).ConfigureAwait(false);
        }
        return await engine.VerifyDatabaseErasureAsync(database, token).ConfigureAwait(false);
    }

    private async ValueTask ConvergePurgesAsync(CancellationToken cancellationToken)
    {
        var page = await ReadAsync<RecordPage?>(async transaction => await transaction.ListAsync(DatabasePurge.Kind, _purgeAfter,
            CatalogPage, MaxPageBytes, cancellationToken).ConfigureAwait(false), static _ => null, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return;
        }

        foreach (var stored in page.Records)
        {
            try
            {
                var record = EntityPayload.Read(stored, ManagementJsonContext.Default.DatabasePurgeRecord)
                    ?? throw new InvalidOperationException("a database purge record is invalid");
                if (record.FormatVersion != 1 || record.Database.DatabaseId != stored.Id || record.ExpectedHolders.Length is 0 or > 1024)
                {
                    throw new InvalidOperationException("a database purge record has unsupported identity or format");
                }

                if (PlatformDatabases.IsPlatformDatabase(record.Database.DatabaseId))
                {
                    if (_skippedPurges.TryAdd((stored.Id, record.RequestedAt), 0))
                    {
                        logger.ZLogWarning($"Skipped a purge record for platform database {stored.Id:@Database} of project {record.Database.ProjectId:@Project}, requested at {record.RequestedAt:@RequestedAt}: a platform database is never purged");
                    }

                    continue;
                }

                var localAbsent = await ReadAsync<bool>(async transaction =>
                    await GetAsync(transaction, DatabasePurge.Receipts(stored.Id), options.MachineId, ManagementJsonContext.Default.DatabasePurgeReceipt, cancellationToken).ConfigureAwait(false) is { } receipt
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
                        logger.ZLogWarning(error, $"Applying permanent database purge {stored.Id:@Database} failed; the next pass tries again");
                    }
                }

                if (!regionalOwners.Writer && record.ExpectedHolders.Contains(options.MachineId, StringComparer.Ordinal))
                {
                    await DeliverReceiptAsync(record, cancellationToken).ConfigureAwait(false);
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
                logger.ZLogWarning(error, $"Applying permanent database purge {stored.Id:@Database} failed; the next pass tries again");
            }
        }

        if (page.HasMore)
        {
            _purgeAfter = page.Records[^1].Id;
            return;
        }

        Interlocked.Exchange(ref _receiptsUndelivered, _receiptsPassUndelivered);
        Interlocked.Exchange(ref _purgePending, _purgePassPending);
        Interlocked.Exchange(ref _purgeOverdue, _purgePassOverdue);
        Interlocked.Exchange(ref _purgeOldestTicks, _purgePassOldestTicks);
        _purgeAfter = null;
        _purgePassPending = _purgePassOverdue = _purgePassOldestTicks = _receiptsPassUndelivered = 0;
    }

    private static async ValueTask<bool> HasDeletionAuthorityAsync(IStorageTransaction transaction, TenantContext database, CancellationToken cancellationToken)
    {
        var purge = await GetAsync(transaction, DatabasePurge.Kind, database.DatabaseId!, ManagementJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
        return purge is not null
            ? purge.FormatVersion == 1 && DatabasePurge.SameDatabase(purge.Database, database)
            : await CatalogTables.RetiredAsync(transaction, database.DatabaseId!, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> TryRecordLocalPurgeAbsenceAsync(IStorageTransaction transaction, TenantContext database, bool erasureVerified, CancellationToken cancellationToken)
    {
        var purge = await GetAsync(transaction, DatabasePurge.Kind, database.DatabaseId!, ManagementJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
        if (purge is null)
        {
            return true;
        }

        if (purge.FormatVersion != 1 || !DatabasePurge.SameDatabase(purge.Database, database))
        {
            throw new InvalidOperationException("a database purge conflicts with its cleanup scope");
        }

        if (!erasureVerified)
        {
            return false;
        }

        await PutAsync(transaction, DatabasePurge.Receipts(database.DatabaseId!), options.MachineId,
            new DatabasePurgeReceipt(database, options.MachineId, time.GetUtcNow(), Authority: purge.RequestedAt), ManagementJsonContext.Default.DatabasePurgeReceipt, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask DeliverReceiptAsync(DatabasePurgeRecord record, CancellationToken cancellationToken)
    {
        var id = record.Database.DatabaseId!;
        var receipt = await ReadAsync<DatabasePurgeReceipt?>(async transaction =>
            await GetAsync(transaction, DatabasePurge.Receipts(id), options.MachineId, ManagementJsonContext.Default.DatabasePurgeReceipt, cancellationToken).ConfigureAwait(false) is { } local
            && local.Binds(record, options.MachineId) ? local : null,
            static _ => null, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            return;
        }

        var key = (id, record.RequestedAt);
        if (regionalOwners.Find(options.Region) is not { } owner || owner.Node == options.MachineId || options.PeersFile is null)
        {
            WaitForWriter(key, "none", "no_writer");
            return;
        }

        var writer = $"{owner.Node}@{owner.Epoch}";
        if (_deliveredReceipts.TryGetValue(key, out var delivered) && delivered == writer)
        {
            return;
        }

        var address = (await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(node => node.Name == owner.Node && node.Region == options.Region)?.InternalListener(options.InternalPort);
        if (address is null)
        {
            WaitForWriter(key, writer, "writer_unknown");
            return;
        }

        switch (await remote.DeliverPurgeReceiptAsync(address, receipt, cancellationToken).ConfigureAwait(false))
        {
            case ReceiptDelivery.Recorded:
                _deliveredReceipts[key] = writer;
                _ = _receiptWaits.TryRemove(key, out _);
                logger.ZLogInformation($"Erasure receipt of database {id:@Database} recorded by regional writer {owner.Node:@Writer} at epoch {owner.Epoch:@Epoch}");
                break;
            case ReceiptDelivery.Refused:
                _deliveredReceipts[key] = writer;
                _ = _receiptWaits.TryRemove(key, out _);
                logger.ZLogWarning($"Regional writer {owner.Node:@Writer} at epoch {owner.Epoch:@Epoch} refused the erasure receipt of database {id:@Database}: its purge record does not name this node for the same deletion; the receipt is offered again when the writer changes");
                break;
            case ReceiptDelivery.Unavailable:
                WaitForWriter(key, writer, "writer_unavailable");
                break;
            default:
                throw new UnreachableException();
        }
    }

    private void WaitForWriter((string Database, DateTimeOffset RequestedAt) key, string writer, string cause)
    {
        _receiptsPassUndelivered++;
        var state = $"{writer} {cause}";
        if (!_receiptWaits.TryGetValue(key, out var logged) || logged != state)
        {
            _receiptWaits[key] = state;
            logger.ZLogWarning($"Erasure receipt of database {key.Database:@Database} waits for the regional writer ({cause:@Cause}, writer {writer:@Writer}); every maintenance pass offers it again");
        }
    }

    internal async ValueTask<PeerReceiptAnswer> RecordPeerReceiptAsync(DatabasePurgeReceipt receipt, CancellationToken cancellationToken)
    {
        if (!regionalOwners.Writer)
        {
            return PeerReceiptAnswer.NotWriter;
        }

        if (receipt.Database?.DatabaseId is not { } id || receipt.MachineId == options.MachineId)
        {
            return PeerReceiptAnswer.Unbound;
        }

        return await WriteAsync<PeerReceiptAnswer>(async transaction =>
        {
            if (await GetAsync(transaction, DatabasePurge.Kind, id, ManagementJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false) is not { FormatVersion: 1 } purge
                || !DatabasePurge.SameDatabase(purge.Database, receipt.Database))
            {
                return PeerReceiptAnswer.Unknown;
            }

            if (!purge.ExpectedHolders.Contains(receipt.MachineId, StringComparer.Ordinal) || !receipt.Binds(purge, receipt.MachineId))
            {
                return PeerReceiptAnswer.Unbound;
            }

            if (await GetAsync(transaction, DatabasePurge.Receipts(id), receipt.MachineId, ManagementJsonContext.Default.DatabasePurgeReceipt, cancellationToken).ConfigureAwait(false) is not { } stored
                || !stored.Binds(purge, receipt.MachineId))
            {
                await PutAsync(transaction, DatabasePurge.Receipts(id), receipt.MachineId, receipt, ManagementJsonContext.Default.DatabasePurgeReceipt, cancellationToken).ConfigureAwait(false);
                logger.ZLogInformation($"Recorded the erasure receipt of database {id:@Database} from holder {receipt.MachineId:@Holder}");
            }

            return PeerReceiptAnswer.Recorded;
        }, static _ => PeerReceiptAnswer.NotWriter, cancellationToken).ConfigureAwait(false);
    }
}

internal enum PeerReceiptAnswer
{
    Recorded,
    Unknown,
    Unbound,
    NotWriter,
}
