using Cronos;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Features.Platform;
using TerraBase.Features.Qos;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Backups;

internal sealed partial class EngineManagedBackupService(
    IStorageEngine engine, SystemCatalog catalog, IManagementService management, IPointInTimeRestore pointInTime, IDatabaseRewind rewind, TimeProvider time,
    ILogger<EngineManagedBackupService> logger, TerraBaseOptions options, Lazy<IBackupKeyShredding> shredding, IHostApplicationLifetime lifetime, IMemoryBudget memoryBudget)
    : IManagedBackupService
{
    public const string BackedUpDatabases = "backed_up_databases";

    private const string Manual = "manual";
    private const string Scheduled = "scheduled";
    private const string CurrentPolicy = "current";
    private const string LastSchedule = "last";
    private const string BackupSequence = "backup_sequence";

    private const int MaxScheduledBackupsPerPass = 16;

    private const int PageSize = 100;

    private const long MaxPageBytes = 1024 * 1024;

    private static readonly JsonTypeInfo<string> Id = TerraBaseJsonContext.Default.String;

    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    private string? _sweepCursor;


    public static BackupPolicy DefaultPolicy { get; } = new("0 2 * * *", 7, null);

    public ValueTask<ListBackupsResult> ListBackupsAsync(
        string organizationId, string databaseId, string? after, int limit, CancellationToken cancellationToken) =>
        catalog.ReadAsync<ListBackupsResult>(async transaction =>
        {
            var page = await transaction.ListAsync(BackupCatalog.List(organizationId, databaseId), after, limit, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            var backups = new List<DatabaseBackup>(page.Records.Count);
            foreach (var entry in page.Records)
            {
                var record = await GetRecordAsync(transaction, organizationId, JsonSerializer.Deserialize(entry.Json.Span, Id)!, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("the backup list names a missing backup");
                backups.Add(record.ToBackup());
            }

            return new ManagementPage<DatabaseBackup>(backups, page.HasMore ? page.Records[^1].Id : null);
        }, static error => error, cancellationToken);

    public async ValueTask<CreateBackupResult> CreateBackupAsync(
        string userId, string organizationId, TenantContext database, CancellationToken cancellationToken)
    {
        var record = NewRecord(DatabaseOwner.Of(database), Manual);
        _inFlight.TryAdd(record.Id, 0);
        try
        {
            return await catalog.WriteAsync<RecordPendingResult>(async transaction =>
            {
                if (await EngineManagementService.GetDatabaseAsync(transaction, userId, organizationId, database.ProjectId!, database.DatabaseId!, cancellationToken).ConfigureAwait(false) is null)
                {
                    return new NotFound();
                }

                await RecordPendingAsync(transaction, record, cancellationToken).ConfigureAwait(false);
                return new Done();
            }, static error => error, cancellationToken).ConfigureAwait(false) switch
            {
                Done => await SnapshotAsync(userId, record, cancellationToken).ConfigureAwait(false),
                NotFound error => error,
                Unavailable error => error,
            };
        }
        finally
        {
            _inFlight.TryRemove(record.Id, out _);
        }
    }

    public async ValueTask<GetBackupPolicyResult> GetPolicyAsync(string databaseId, CancellationToken cancellationToken) =>
        await catalog.ReadAsync<GetBackupPolicyResult>(async transaction =>
        {
            var policy = await GetPolicyAsync(transaction, databaseId, cancellationToken).ConfigureAwait(false);
            var schedule = await GetAsync(transaction, BackupCatalog.Schedule(databaseId), LastSchedule, ManagementJsonContext.Default.BackupSchedule, cancellationToken).ConfigureAwait(false);
            return new BackupPolicyStatus(
                policy.FullBackupCron, policy.RetentionDays, policy.UpdatedAt,
                schedule?.LastSucceededAt,
                schedule?.LastFailedAt,
                schedule is { LastErrorCode: { } code, LastErrorMessage: { } message } ? new BackupPolicyError(code, message) : null,
                NextDueAt(policy, time.GetUtcNow()));
        }, static error => error, cancellationToken)
            .ConfigureAwait(false);

    private static DateTimeOffset? NextDueAt(BackupPolicy policy, DateTimeOffset now)
    {
        if (policy is not { FullBackupCron: { } expression }
            || !CronExpression.TryParse(expression, CronFormat.Standard, out var cron))
        {
            return null;
        }

        return cron.GetNextOccurrence(now, TimeZoneInfo.Utc);
    }

    public async ValueTask<SetBackupPolicyResult> SetPolicyAsync(
        string userId, string organizationId, TenantContext database, string? fullBackupCron, int retentionDays, CancellationToken cancellationToken)
    {
        var policy = new BackupPolicy(fullBackupCron, retentionDays, time.GetUtcNow());
        var owner = DatabaseOwner.Of(database);
        return await catalog.WriteAsync<SetBackupPolicyResult>(async transaction =>
        {
            if (await EngineManagementService.GetDatabaseAsync(transaction, userId, organizationId, owner.ProjectId, owner.DatabaseId, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            var access = await EngineManagementService.WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not Organization)
            {
                return access switch
                {
                    Forbidden error => error,
                    NotFound => new NotFound(),
                    Organization => throw new UnreachableException(),
                };
            }

            await PutAsync(transaction, BackupCatalog.Policy(owner.DatabaseId), CurrentPolicy, policy, ManagementJsonContext.Default.BackupPolicy, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, BackedUpDatabases, owner.DatabaseId, owner, ManagementJsonContext.Default.DatabaseOwner, cancellationToken).ConfigureAwait(false);
            return policy;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RunScheduleAsync(CancellationToken cancellationToken)
    {
        if (await catalog.CheckAsync(cancellationToken).ConfigureAwait(false) is Unavailable)
        {
            return;
        }

        await RecoverInterruptedRestoresAsync(cancellationToken).ConfigureAwait(false);

        if (await SweepAsync(cancellationToken).ConfigureAwait(false) is Unavailable)
        {
            return;
        }

        var now = time.GetUtcNow();
        var due = new List<(DateTimeOffset Turn, bool Failed, DatabaseOwner Owner)>();
        string? after = null;
        do
        {
            var listed = await catalog.ReadAsync<CatalogResult<ManagementPage<DatabaseOwner>>>(
                async transaction => await ListAsync(transaction, BackedUpDatabases, after, PageSize, ManagementJsonContext.Default.DatabaseOwner, owner => owner.DatabaseId, cancellationToken)
                    .ConfigureAwait(false),
                static error => error, cancellationToken).ConfigureAwait(false);
            if (listed is not ManagementPage<DatabaseOwner> page)
            {
                return;
            }
            foreach (var owner in page.Items)
            {
                try
                {
                    var selected = await ExpireAndTurnAsync(owner, now, cancellationToken).ConfigureAwait(false);
                    if (selected is Unavailable)
                    {
                        return;
                    }

                    if (selected is BackupTurn turn)
                    {
                        due.Add((turn.Turn, turn.Failed, owner));
                    }
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.ZLogWarning(error, $"Scheduled backup work for database {owner.DatabaseId:@DatabaseId} failed");
                }
            }

            due = [.. due.OrderBy(item => item.Turn).ThenBy(item => item.Failed).ThenBy(item => item.Owner.DatabaseId, StringComparer.Ordinal).Take(MaxScheduledBackupsPerPass)];
            after = page.Next;
        }
        while (after is not null);

        if (due.Count == 0)
        {
            return;
        }

        var records = new List<BackupRecord>(due.Count);
        try
        {
            var pending = await catalog.WriteAsync<CatalogCheck>(async transaction =>
            {
                foreach (var (_, _, owner) in due)
                {
                    if (await EngineManagementService.DatabaseTenantUncheckedAsync(transaction, owner.OrganizationId, owner.ProjectId, owner.DatabaseId, cancellationToken)
                        .ConfigureAwait(false) is not null
                        && await EngineManagementService.DatabaseUncheckedAsync(transaction, owner.ProjectId, owner.DatabaseId, cancellationToken).ConfigureAwait(false) is { } database)
                    {
                        var policy = await GetPolicyAsync(transaction, owner.DatabaseId, cancellationToken).ConfigureAwait(false);
                        var schedule = await GetAsync(transaction, BackupCatalog.Schedule(owner.DatabaseId), LastSchedule, ManagementJsonContext.Default.BackupSchedule, cancellationToken)
                            .ConfigureAwait(false);
                        if (database.Home(options.Region) != options.Region || DueSince(policy, schedule, database.CreatedAt, time.GetUtcNow()) is null)
                        {
                            continue;
                        }

                        var record = NewRecord(owner, Scheduled);
                        _inFlight.TryAdd(record.Id, 0);
                        records.Add(record);
                        await RecordPendingAsync(transaction, record, cancellationToken).ConfigureAwait(false);
                    }
                }

                return new Done();
            }, static error => error, cancellationToken).ConfigureAwait(false);
            if (pending is Unavailable)
            {
                return;
            }

            foreach (var record in records)
            {
                try
                {
                    _ = await SnapshotAsync(userId: null, record, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    logger.ZLogWarning(error, $"Scheduled backup work for database {record.DatabaseId:@DatabaseId} failed");
                }
            }
        }
        finally
        {
            foreach (var record in records)
            {
                _inFlight.TryRemove(record.Id, out _);
            }
        }
    }

    private async ValueTask<CreateBackupResult> SnapshotAsync(string? userId, BackupRecord record, CancellationToken cancellationToken)
    {
        SnapshotSourceResult found;
        try
        {
            found = await catalog.WriteAsync<SnapshotSourceResult>(async transaction =>
            {
                if (await GetRecordAsync(transaction, record.OrganizationId, record.Id, cancellationToken).ConfigureAwait(false) is not { State: BackupState.Pending } current)
                {
                    return new NotFound();
                }

                if (await GetDatabaseAsync(transaction, userId, current, cancellationToken).ConfigureAwait(false) is not { } database)
                {
                    await ExpireAsync(transaction, current, cancellationToken).ConfigureAwait(false);
                    return new NotFound();
                }

                await PutRecordAsync(transaction, current with { State = BackupState.Snapshotting }, cancellationToken).ConfigureAwait(false);
                return database;
            }, static error => error, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await FailAsync(record, "snapshot_failed", "Taking the snapshot failed").ConfigureAwait(false);
            throw;
        }

        if (found is Unavailable refused)
        {
            return refused;
        }

        if (found is not TenantContext source)
        {
            _ = await SweepBackupAsync(record.OrganizationId, record.Id).ConfigureAwait(false);
            return new NotFound();
        }

        RegisteredSnapshotResult registered;
        try
        {
            var snapshot = record.Storage(source.Plan);
            if (!await engine.CopyDatabaseAsync(source, snapshot, cancellationToken).ConfigureAwait(false))
            {
                await FailAsync(record, "source_missing", "The database's files could not be read").ConfigureAwait(false);
                return new NotFound();
            }

            var size = await engine.GetDatabaseSizeAsync(snapshot, cancellationToken).ConfigureAwait(false) ?? 0;
            registered = await catalog.WriteAsync<RegisteredSnapshotResult>(
                async transaction => await RegisterAsync(transaction, userId, record, size, cancellationToken).ConfigureAwait(false) is { } completed ? completed : new NotFound(),
                static error => error, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await FailAsync(record, "snapshot_failed", "Taking the snapshot failed").ConfigureAwait(false);
            throw;
        }

        if (registered is Unavailable unavailable)
        {
            return unavailable;
        }

        if (registered is not BackupRecord ready)
        {
            _ = await SweepBackupAsync(record.OrganizationId, record.Id).ConfigureAwait(false);
            return new NotFound();
        }

        return ready.ToBackup();
    }

    private async ValueTask<BackupRecord?> RegisterAsync(IStorageTransaction transaction, string? userId, BackupRecord record, long size, CancellationToken cancellationToken)
    {
        if (await GetRecordAsync(transaction, record.OrganizationId, record.Id, cancellationToken).ConfigureAwait(false) is not { State: BackupState.Snapshotting } current)
        {
            return null;
        }

        if (await GetDatabaseAsync(transaction, userId, current, cancellationToken).ConfigureAwait(false) is null)
        {
            await ExpireAsync(transaction, current, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var now = time.GetUtcNow();
        var order = EngineManagementService.AscendingKey(
            await EngineManagementService.NextSequenceAsync(transaction, EngineManagementService.State(current.OrganizationId), BackupSequence, cancellationToken).ConfigureAwait(false));
        var done = current with { State = BackupState.Registered, CompletedAt = now, SizeBytes = size, Order = order };
        await PutRecordAsync(transaction, done, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, BackupCatalog.List(done.OrganizationId, done.DatabaseId), order, done.Id, Id, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, BackupCatalog.Retention(done.OrganizationId, done.DatabaseId), RetentionKey(now, order), done.Id, Id, cancellationToken).ConfigureAwait(false);
        await StorageSweep.RemoveAsync(transaction, SweepOf(done), cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, BackedUpDatabases, done.DatabaseId, new DatabaseOwner(done.OrganizationId, done.ProjectId, done.DatabaseId), ManagementJsonContext.Default.DatabaseOwner, cancellationToken)
            .ConfigureAwait(false);
        if (done.Trigger == Scheduled)
        {
            await PutAsync(transaction, BackupCatalog.Schedule(done.DatabaseId), LastSchedule, new BackupSchedule(now, null, null, null), ManagementJsonContext.Default.BackupSchedule, cancellationToken)
                .ConfigureAwait(false);
        }

        return done;
    }

    private async ValueTask FailAsync(BackupRecord record, string errorCode, string errorMessage)
    {
        try
        {
            var settled = await catalog.WriteAsync<CatalogCheck>(async transaction =>
            {
                if (await GetRecordAsync(transaction, record.OrganizationId, record.Id, CancellationToken.None).ConfigureAwait(false) is { State: BackupState.Pending or BackupState.Snapshotting } current)
                {
                    await ExpireAsync(transaction, current, CancellationToken.None).ConfigureAwait(false);
                }

                if (record.Trigger == Scheduled
                    && await EngineManagementService.DatabaseTenantUncheckedAsync(transaction, record.OrganizationId, record.ProjectId, record.DatabaseId, CancellationToken.None)
                        .ConfigureAwait(false) is not null)
                {
                    var schedule = await GetAsync(transaction, BackupCatalog.Schedule(record.DatabaseId), LastSchedule, ManagementJsonContext.Default.BackupSchedule, CancellationToken.None)
                        .ConfigureAwait(false);
                    var failure = (schedule ?? new BackupSchedule(null, null, null, null)) with { LastFailedAt = time.GetUtcNow(), LastErrorCode = errorCode, LastErrorMessage = errorMessage };
                    await PutAsync(transaction, BackupCatalog.Schedule(record.DatabaseId), LastSchedule, failure, ManagementJsonContext.Default.BackupSchedule, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                return new Done();
            }, static error => error, CancellationToken.None).ConfigureAwait(false);
            if (settled is Unavailable)
            {
                return;
            }
        }
        catch (Exception error)
        {
            logger.ZLogWarning(error, $"Recording the failure of backup {record.Id:@BackupId} failed; a later storage sweep settles it");
            return;
        }

        _ = await SweepBackupAsync(record.OrganizationId, record.Id).ConfigureAwait(false);
    }

    private async ValueTask<BackupTurnResult> ExpireAndTurnAsync(DatabaseOwner owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var selected = await catalog.ReadAsync<CatalogResult<RetentionState>>(
            async transaction => new RetentionState(
                await EngineManagementService.DatabaseUncheckedAsync(transaction, owner.ProjectId, owner.DatabaseId, cancellationToken).ConfigureAwait(false),
                await GetPolicyAsync(transaction, owner.DatabaseId, cancellationToken).ConfigureAwait(false),
                await GetAsync(transaction, BackupCatalog.Schedule(owner.DatabaseId), LastSchedule, ManagementJsonContext.Default.BackupSchedule, cancellationToken).ConfigureAwait(false),
                await OldestRegisteredAsync(transaction, owner, cancellationToken).ConfigureAwait(false)),
            static error => error, cancellationToken).ConfigureAwait(false);
        if (selected is not RetentionState state)
        {
            return selected switch
            {
                Unavailable error => error,
                RetentionState => throw new UnreachableException(),
            };
        }

        var (database, policy, schedule, oldest) = state;

        if (oldest?.CompletedAt is { } completed && completed < now.AddDays(-policy.RetentionDays))
        {
            var retained = await catalog.WriteAsync<CatalogResult<List<BackupRecord>>>(async transaction =>
            {
                if (await transaction.GetAsync(DeletedDatabases.Kind, owner.DatabaseId, cancellationToken).ConfigureAwait(false) is not null)
                {
                    return new List<BackupRecord>();
                }
                var current = await GetPolicyAsync(transaction, owner.DatabaseId, cancellationToken).ConfigureAwait(false);
                var cutoff = time.GetUtcNow().AddDays(-current.RetentionDays);
                var expired = new List<BackupRecord>();
                var page = await transaction.ListAsync(BackupCatalog.Retention(owner.OrganizationId, owner.DatabaseId), afterId: null, PageSize, MaxPageBytes, cancellationToken).ConfigureAwait(false);
                foreach (var entry in page.Records)
                {
                    var record = await GetRecordAsync(transaction, owner.OrganizationId, JsonSerializer.Deserialize(entry.Json.Span, Id)!, cancellationToken).ConfigureAwait(false);
                    if (record is not { State: BackupState.Registered, CompletedAt: { } at } || at >= cutoff)
                    {
                        break;
                    }

                    if (await LeaseActiveAsync(transaction, record, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    await ExpireAsync(transaction, record, cancellationToken).ConfigureAwait(false);
                    expired.Add(record);
                }

                return expired;
            }, static error => error, cancellationToken).ConfigureAwait(false);
            if (retained is not List<BackupRecord> expired)
            {
                return retained switch
                {
                    Unavailable error => error,
                    List<BackupRecord> => throw new UnreachableException(),
                };
            }

            foreach (var record in expired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await SweepBackupAsync(record.OrganizationId, record.Id).ConfigureAwait(false) is Unavailable refused)
                {
                    return refused;
                }
            }
        }

        return database is null || database.Home(options.Region) != options.Region || DueSince(policy, schedule, database.CreatedAt, now) is not { } since ? new NotFound()
            : schedule?.LastFailedAt is { } failedAt && failedAt >= since ? new BackupTurn(failedAt, true)
            : new BackupTurn(since, false);
    }

    private async ValueTask<CatalogCheck> SweepAsync(CancellationToken cancellationToken)
    {
        var listed = await catalog.ReadAsync<CatalogResult<ManagementPage<SweepEntry>>>(
            async transaction => await StorageSweep.ListAsync(transaction, _sweepCursor, PageSize, cancellationToken).ConfigureAwait(false),
            static error => error, cancellationToken).ConfigureAwait(false);
        if (listed is not ManagementPage<SweepEntry> page)
        {
            return listed switch
            {
                Unavailable error => error,
                ManagementPage<SweepEntry> => throw new UnreachableException(),
            };
        }

        foreach (var entry in page.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (entry.Kind)
                {
                    case StorageSweep.Backup:
                        if (await SweepBackupAsync(entry.OrganizationId, entry.Id).ConfigureAwait(false) is Unavailable backupRefused)
                        {
                            return backupRefused;
                        }

                        break;
                    case StorageSweep.DatabaseBackups:
                        if (await ExpireDatabaseBackupsAsync(entry, cancellationToken).ConfigureAwait(false) is Unavailable databaseRefused)
                        {
                            return databaseRefused;
                        }

                        break;
                    default:
                        throw new InvalidOperationException("unknown storage sweep entry");
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Storage sweep of {entry.Kind:@Kind} {entry.Id:@Id} failed; it is retried on a later pass");
            }
        }

        _sweepCursor = page.Next;
        return new Done();
    }

    private async ValueTask<CatalogCheck> ExpireDatabaseBackupsAsync(SweepEntry entry, CancellationToken cancellationToken)
    {
        var expiring = await catalog.WriteAsync<CatalogResult<List<BackupRecord>>>(async transaction =>
        {
            var page = await transaction.ListAsync(BackupCatalog.List(entry.OrganizationId, entry.Id), afterId: null, PageSize, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            var expired = new List<BackupRecord>(page.Records.Count);
            var leased = false;
            foreach (var listed in page.Records)
            {
                if (await GetRecordAsync(transaction, entry.OrganizationId, JsonSerializer.Deserialize(listed.Json.Span, Id)!, cancellationToken).ConfigureAwait(false) is { } record)
                {
                    if (await LeaseActiveAsync(transaction, record, cancellationToken).ConfigureAwait(false))
                    {
                        leased = true;
                        continue;
                    }

                    await ExpireAsync(transaction, record, cancellationToken).ConfigureAwait(false);
                    expired.Add(record);
                }
            }

            if (!page.HasMore && !leased)
            {
                await StorageSweep.RemoveAsync(transaction, entry, cancellationToken).ConfigureAwait(false);
            }

            return expired;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (expiring is not List<BackupRecord> expired)
        {
            return expiring switch
            {
                Unavailable error => error,
                List<BackupRecord> => throw new UnreachableException(),
            };
        }

        foreach (var record in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await SweepBackupAsync(record.OrganizationId, record.Id).ConfigureAwait(false) is Unavailable refused)
            {
                return refused;
            }
        }

        return new Done();
    }

    private async ValueTask<CatalogCheck> SweepBackupAsync(string organizationId, string backupId)
    {
        var entry = new SweepEntry(StorageSweep.Backup, organizationId, backupId);
        var selected = await catalog.WriteAsync<CatalogResult<SweepBackupState>>(async transaction =>
        {
            var plan = await EngineManagementService.PlanUncheckedAsync(transaction, organizationId, CancellationToken.None).ConfigureAwait(false);
            var record = await GetRecordAsync(transaction, organizationId, backupId, CancellationToken.None).ConfigureAwait(false);
            if (record?.Region is { } region && region != options.Region)
            {
                return new SweepBackupState(null, plan);
            }

            switch (record?.State)
            {
                case null or BackupState.Registered:
                    await StorageSweep.RemoveAsync(transaction, entry, CancellationToken.None).ConfigureAwait(false);
                    return new SweepBackupState(null, plan);
                case BackupState.Pending or BackupState.Snapshotting when _inFlight.ContainsKey(record.Id):
                    return new SweepBackupState(null, plan);
                case BackupState.Pending or BackupState.Snapshotting:
                    if (record.Trigger == Scheduled
                        && await EngineManagementService.DatabaseTenantUncheckedAsync(transaction, organizationId, record.ProjectId, record.DatabaseId, CancellationToken.None)
                            .ConfigureAwait(false) is not null)
                    {
                        var schedule = await GetAsync(transaction, BackupCatalog.Schedule(record.DatabaseId), LastSchedule, ManagementJsonContext.Default.BackupSchedule, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!(schedule?.LastSucceededAt >= record.StartedAt) && !(schedule?.LastFailedAt >= record.StartedAt))
                        {
                            var failure = (schedule ?? new BackupSchedule(null, null, null, null)) with { LastFailedAt = record.StartedAt, LastErrorCode = "abandoned", LastErrorMessage = "The scheduled attempt did not finish; its process stopped mid-way" };
                            await PutAsync(transaction, BackupCatalog.Schedule(record.DatabaseId), LastSchedule, failure, ManagementJsonContext.Default.BackupSchedule, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    }

                    record = record with { State = BackupState.Expiring };
                    await PutRecordAsync(transaction, record, CancellationToken.None).ConfigureAwait(false);
                    return new SweepBackupState(record, plan);
                default:
                    if (await LeaseActiveAsync(transaction, record, CancellationToken.None).ConfigureAwait(false))
                    {
                        return new SweepBackupState(null, plan);
                    }

                    return new SweepBackupState(record, plan);
            }
        }, static error => error, CancellationToken.None).ConfigureAwait(false);
        if (selected is not SweepBackupState state)
        {
            return selected switch
            {
                Unavailable error => error,
                SweepBackupState => throw new UnreachableException(),
            };
        }

        var (record, plan) = state;

        if (record is null)
        {
            return new Done();
        }

        var storage = record.Storage(plan);
        if (!await shredding.Value.DestroyAsync(storage, DateTimeOffset.MaxValue, CancellationToken.None).ConfigureAwait(false)
            || !await StorageSweep.DeleteFilesAsync(engine, storage, logger).ConfigureAwait(false))
        {
            return new Done();
        }

        return await catalog.WriteAsync<CatalogCheck>(async transaction =>
        {
            if (await GetRecordAsync(transaction, organizationId, backupId, CancellationToken.None).ConfigureAwait(false) is { State: BackupState.Expiring })
            {
                await transaction.DeleteAsync(BackupCatalog.Records(organizationId), backupId, CancellationToken.None).ConfigureAwait(false);
                await StorageSweep.RemoveAsync(transaction, entry, CancellationToken.None).ConfigureAwait(false);
            }

            return new Done();
        }, static error => error, CancellationToken.None).ConfigureAwait(false);
    }

    private static async ValueTask ExpireAsync(IStorageTransaction transaction, BackupRecord record, CancellationToken cancellationToken)
    {
        if (record is { Order: { } order, CompletedAt: { } completedAt })
        {
            await transaction.DeleteAsync(BackupCatalog.List(record.OrganizationId, record.DatabaseId), order, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(BackupCatalog.Retention(record.OrganizationId, record.DatabaseId), RetentionKey(completedAt, order), cancellationToken).ConfigureAwait(false);
        }

        await PutRecordAsync(transaction, record with { State = BackupState.Expiring }, cancellationToken).ConfigureAwait(false);
        await StorageSweep.AddAsync(transaction, SweepOf(record), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RecordPendingAsync(IStorageTransaction transaction, BackupRecord record, CancellationToken cancellationToken)
    {
        await PutRecordAsync(transaction, record, cancellationToken).ConfigureAwait(false);
        await StorageSweep.AddAsync(transaction, SweepOf(record), cancellationToken).ConfigureAwait(false);
    }

    private BackupRecord NewRecord(DatabaseOwner database, string trigger)
    {
        var now = time.GetUtcNow();
        return new BackupRecord(
            EntityIds.New(time).ToString(), database.OrganizationId, database.ProjectId, database.DatabaseId, BackupState.Pending, trigger, now, null, 0, null, options.Region);
    }

    private static async ValueTask<BackupRecord?> OldestRegisteredAsync(IStorageTransaction transaction, DatabaseOwner owner, CancellationToken cancellationToken)
    {
        var first = await transaction.ListAsync(BackupCatalog.Retention(owner.OrganizationId, owner.DatabaseId), afterId: null, limit: 1, MaxPageBytes, cancellationToken).ConfigureAwait(false);
        return first.Records.Count == 0
            ? null
            : await GetRecordAsync(transaction, owner.OrganizationId, JsonSerializer.Deserialize(first.Records[0].Json.Span, Id)!, cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask<TenantContext?> GetDatabaseAsync(IStorageTransaction transaction, string? userId, BackupRecord record, CancellationToken cancellationToken) =>
        userId is null
            ? EngineManagementService.DatabaseTenantUncheckedAsync(transaction, record.OrganizationId, record.ProjectId, record.DatabaseId, cancellationToken)
            : EngineManagementService.AuthorizedDatabaseTenantAsync(transaction, userId, record.OrganizationId, record.ProjectId, record.DatabaseId, cancellationToken);

    private static ValueTask<BackupRecord?> GetRecordAsync(IStorageTransaction transaction, string organizationId, string backupId, CancellationToken cancellationToken) =>
        GetAsync(transaction, BackupCatalog.Records(organizationId), backupId, ManagementJsonContext.Default.BackupRecord, cancellationToken);

    private static ValueTask PutRecordAsync(IStorageTransaction transaction, BackupRecord record, CancellationToken cancellationToken) =>
        PutAsync(transaction, BackupCatalog.Records(record.OrganizationId), record.Id, record, ManagementJsonContext.Default.BackupRecord, cancellationToken);

    private static SweepEntry SweepOf(BackupRecord record) => new(StorageSweep.Backup, record.OrganizationId, record.Id);

    private static string RetentionKey(DateTimeOffset completedAt, string order) => EngineManagementService.TicksKey(completedAt) + order;

    private static DateTimeOffset? DueSince(BackupPolicy policy, BackupSchedule? schedule, DateTimeOffset createdAt, DateTimeOffset now)
    {
        if (policy is not { FullBackupCron: { } expression }
            || !CronExpression.TryParse(expression, CronFormat.Standard, out var cron))
        {
            return null;
        }

        var saved = policy.UpdatedAt ?? createdAt;
        var since = schedule is { LastSucceededAt: { } last } && last > saved ? last : saved;
        return cron.GetNextOccurrence(since, TimeZoneInfo.Utc) is { } next && next <= now ? next : null;
    }

    private static async ValueTask<BackupPolicy> GetPolicyAsync(IStorageTransaction transaction, string databaseId, CancellationToken cancellationToken) =>
        await GetAsync(transaction, BackupCatalog.Policy(databaseId), CurrentPolicy, ManagementJsonContext.Default.BackupPolicy, cancellationToken).ConfigureAwait(false)
        ?? DefaultPolicy;

    private readonly union RecordPendingResult(Done, NotFound, Unavailable);

    private readonly union CatalogResult<T>(T, Unavailable);

    private readonly union SnapshotSourceResult(TenantContext, NotFound, Unavailable);

    private readonly union RegisteredSnapshotResult(BackupRecord, NotFound, Unavailable);

    private sealed record RetentionState(ManagedDatabase? Database, BackupPolicy Policy, BackupSchedule? Schedule, BackupRecord? Oldest);

    private sealed record BackupTurn(DateTimeOffset Turn, bool Failed);

    private readonly union BackupTurnResult(BackupTurn, NotFound, Unavailable);

    private sealed record SweepBackupState(BackupRecord? Record, PlanTier Plan);

    private sealed record RestoreSource(ManagedDatabase Database, string StorageId, string? Region);

    private readonly union RestoreSourceResult(RestoreSource, NotFound, Unavailable);
}
