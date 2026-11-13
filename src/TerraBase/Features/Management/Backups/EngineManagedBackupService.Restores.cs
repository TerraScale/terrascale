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

internal sealed partial class EngineManagedBackupService
{
    private const int MaxDatabaseNameLength = 63;
    private const int MaxRestoreNameAttempts = 100;

    private static readonly TimeSpan RestoreLeaseExpiry = TimeSpan.FromHours(1);

    private static readonly TimeSpan RestoreLeaseHeartbeat = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, byte> _runningRestores = new();

    private readonly DateTimeOffset _startedAt = time.GetUtcNow();
    public async ValueTask<StartRestoreResult> StartRestoreAsync(
        string userId, string organizationId, TenantContext database, string backupId, string? name, CancellationToken cancellationToken)
    {
        if (PlatformDatabases.IsPlatform(database.OrganizationId))
        {
            return ErasureFenced();
        }

        var owner = DatabaseOwner.Of(database);
        return await catalog.ReadAsync<RestoreSourceResult>(async transaction =>
                await GetRecordAsync(transaction, organizationId, backupId, cancellationToken).ConfigureAwait(false) is { State: BackupState.Registered } record
                && record.DatabaseId == owner.DatabaseId
                && await EngineManagementService.GetDatabaseAsync(transaction, userId, organizationId, owner.ProjectId, owner.DatabaseId, cancellationToken)
                    .ConfigureAwait(false) is { } source
                    ? new RestoreSource(source, record.Id, record.Region)
                    : new NotFound(),
                static error => error, cancellationToken).ConfigureAwait(false) switch
        {
            RestoreSource { Region: null } => new Unavailable("the snapshot's region is unknown; take a new snapshot", "backup_region_unknown"),
            RestoreSource source when source.Region != options.Region => new BackupElsewhere(source.Region!, source.Database),
            RestoreSource source when !source.Database.PlacedIn(options.Region) => new Conflict("the snapshot's region is outside the database's current placement", "backup_region_not_placed"),
            RestoreSource source => await AdmitBackupRestoreAsync(userId, organizationId, owner, source, name, cancellationToken),
            NotFound error => error,
            Unavailable error => error,
        };
    }

    private async ValueTask<StartRestoreResult> AdmitBackupRestoreAsync(
        string userId, string organizationId, DatabaseOwner owner, RestoreSource source, string? name, CancellationToken cancellationToken)
    {
        var prechecked = await management.PrecheckRestoreAsync(userId, organizationId, owner.ProjectId, name, cancellationToken).ConfigureAwait(false);
        if (prechecked is not Done)
        {
            return prechecked switch
            {
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
                Unavailable error => error,
                Done => throw new UnreachableException(),
            };
        }

        return await StartOperationAsync(
            organizationId, owner, RestoreKind.Backup, name,
            stopping => CopyRestoredAsync(userId, organizationId, owner, source.Database.Name, source.StorageId, name, stopping),
            cancellationToken, source.StorageId);
    }

    public async ValueTask<StartRestoreResult> StartRestorePointInTimeAsync(
        string userId, string organizationId, TenantContext database, DateTimeOffset at, string? name, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform,
        long? ttlSeconds = null)
    {
        if (PlatformDatabases.IsPlatform(database.OrganizationId))
        {
            return ErasureFenced();
        }

        var owner = DatabaseOwner.Of(database);
        var found = await catalog.ReadAsync<GetDatabaseResult>(
            async transaction => await EngineManagementService.GetDatabaseAsync(transaction, userId, organizationId, owner.ProjectId, owner.DatabaseId, cancellationToken)
                .ConfigureAwait(false) is { } database ? database : new NotFound(),
            static error => error, cancellationToken).ConfigureAwait(false);
        if (found is not ManagedDatabase source)
        {
            return found switch
            {
                NotFound error => error,
                Unavailable error => error,
                ManagedDatabase => throw new UnreachableException(),
            };
        }

        if (source.Home(options.Region) != options.Region)
        {
            return new BackupElsewhere(source.Home(options.Region), source);
        }

        var prechecked = await management.PrecheckRestoreAsync(userId, organizationId, owner.ProjectId, name, cancellationToken).ConfigureAwait(false);
        if (prechecked is not Done)
        {
            return prechecked switch
            {
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
                Unavailable error => error,
                Done => throw new UnreachableException(),
            };
        }

        var planned = await pointInTime.PlanAsync(database, at, cancellationToken, destination).ConfigureAwait(false);
        if (planned is Validation outside)
        {
            return outside;
        }

        if (planned is Conflict { Gap: not null } lost)
        {
            return lost;
        }

        var leased = planned is PointInTimePlan { Generation: { } generation } ? generation : (Ulid?)null;

        return await StartOperationAsync(
            organizationId, owner, RestoreKind.PointInTime, name,
            stopping => FromStagingRestoredAsync(userId, organizationId, database, at, destination, source.Name, name, ttlSeconds, stopping),
            cancellationToken, leasedDatabaseId: database.DatabaseId, leasedGeneration: leased);
    }

    private async ValueTask<RestoreBackupResult> FromStagingRestoredAsync(
        string userId, string organizationId, TenantContext database, DateTimeOffset at, string destination, string sourceName, string? name, long? ttlSeconds, CancellationToken cancellationToken)
    {
        var staging = database with { DatabaseId = EntityIds.New().ToString() };
        try
        {
            return await pointInTime.RestoreAsync(database, at, staging, cancellationToken, destination).ConfigureAwait(false) switch
            {
                Done => await CopyRestoredAsync(userId, organizationId, DatabaseOwner.Of(database), sourceName, staging.DatabaseId!, name, cancellationToken, ttlSeconds).ConfigureAwait(false),
                NotFound error => error,
                Conflict error => error,
                Validation error => error,
                Unavailable error => error,
            };
        }
        finally
        {
            await engine.DeleteDatabaseAsync(staging, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask<GetRestoreOperationResult> GetRestoreOperationAsync(
        string organizationId, string databaseId, string operationId, CancellationToken cancellationToken) =>
        await catalog.ReadAsync<GetRestoreOperationResult>(
            async transaction => await GetRestoreOperationRecordAsync(transaction, organizationId, databaseId, operationId, cancellationToken).ConfigureAwait(false) is { } record
                ? record
                : new NotFound(),
            static error => error, cancellationToken).ConfigureAwait(false);

    private static async ValueTask<RestoreOperationRecord?> GetRestoreOperationRecordAsync(
        IStorageTransaction transaction, string organizationId, string databaseId, string operationId, CancellationToken cancellationToken)
    {
        var record = await GetAsync(transaction, RestoreCatalog.Kind, operationId, ManagementJsonContext.Default.RestoreOperationRecord, cancellationToken).ConfigureAwait(false);
        return record is { OrganizationId: var organization, SourceDatabaseId: var source } && organization == organizationId && source == databaseId
            ? record
            : null;
    }

    private static ValueTask PutRestoreOperationAsync(IStorageTransaction transaction, RestoreOperationRecord record, CancellationToken cancellationToken) =>
        PutAsync(transaction, RestoreCatalog.Kind, record.Id, record, ManagementJsonContext.Default.RestoreOperationRecord, cancellationToken);

    private async ValueTask<StartRestoreResult> StartOperationAsync(
        string organizationId, DatabaseOwner owner, string kind, string? name,
        Func<CancellationToken, ValueTask<RestoreBackupResult>> copy, CancellationToken cancellationToken, string? leasedBackupId = null,
        string? leasedDatabaseId = null, Ulid? leasedGeneration = null)
    {
        var id = EntityIds.New(time).ToString();
        _ = _runningRestores.TryAdd(id, 0);
        var admitted = await catalog.WriteAsync<StartRestoreResult>(async transaction =>
        {
            var running = await SystemCatalog.ListAllAsync(transaction, RestoreCatalog.Kind, ManagementJsonContext.Default.RestoreOperationRecord, cancellationToken)
                .ConfigureAwait(false);
            if (name is not null && running.Any(current => current.State == RestoreOperationState.Running
                    && current.Region == options.Region
                    && current.OrganizationId == organizationId
                    && current.TargetName == name))
            {
                return ManagementErrors.NameTaken;
            }

            if (leasedBackupId is { } backupId)
            {
                if (await GetRecordAsync(transaction, organizationId, backupId, cancellationToken).ConfigureAwait(false) is not { State: BackupState.Registered })
                {
                    return new NotFound();
                }

                await PutLeaseAsync(transaction, organizationId, backupId, id, cancellationToken).ConfigureAwait(false);
            }

            if (leasedDatabaseId is { } sourceId && leasedGeneration is { } sourceGeneration)
            {
                await PutGenerationLeaseAsync(transaction, organizationId, sourceId, sourceGeneration, id, cancellationToken).ConfigureAwait(false);
            }

            var record = new RestoreOperationRecord(
                id, organizationId, owner.ProjectId, owner.DatabaseId, null, kind, RestoreOperationState.Running, name, null, null,
                time.GetUtcNow(), null, options.Region, options.MachineId, leasedBackupId, leasedDatabaseId, leasedGeneration);
            await PutRestoreOperationAsync(transaction, record, cancellationToken).ConfigureAwait(false);
            return record;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (admitted is not RestoreOperationRecord record)
        {
            _ = _runningRestores.TryRemove(id, out _);
            return admitted;
        }

        var access = ProjectAccess.Current;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunRestoreAsync(record, access, copy).ConfigureAwait(false);
                }
                finally
                {
                    _ = _runningRestores.TryRemove(id, out _);
                }
            },
            CancellationToken.None);
        return record;
    }

    private async Task RunRestoreAsync(RestoreOperationRecord record, ProjectAccess? access, Func<CancellationToken, ValueTask<RestoreBackupResult>> copy)
    {
        using var qos = QosContext.Enter(null, memoryBudget);
        using var beating = new CancellationTokenSource();
        var heartbeat = HeartbeatLeaseAsync(record, beating.Token);
        RestoreBackupResult? result = null;
        try
        {
            await ProjectAccess.RunAsync(access, async () =>
            {
                result = await copy(lifetime.ApplicationStopping).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception error) when (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            logger.ZLogWarning(error, $"Restore operation {record.Id:@OperationId} failed");
        }
        catch (Exception) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            beating.Cancel();
            await heartbeat.ConfigureAwait(false);
            await SettleStoppedAsync(record).ConfigureAwait(false);
            return;
        }

        beating.Cancel();
        await heartbeat.ConfigureAwait(false);
        var settled = result switch
        {
            ManagedDatabase restored => record with { State = RestoreOperationState.Completed, TargetDatabaseId = restored.Id, TargetName = restored.Name },
            Conflict error => record with { State = RestoreOperationState.Failed, ErrorCode = error.Code, ErrorMessage = error.Message },
            NotFound error => record with { State = RestoreOperationState.Failed, ErrorCode = error.Code, ErrorMessage = error.Message ?? "the restore found nothing to restore" },
            Forbidden error => record with { State = RestoreOperationState.Failed, ErrorCode = error.Code, ErrorMessage = error.Message },
            Validation error => record with { State = RestoreOperationState.Failed, ErrorCode = error.Code, ErrorMessage = error.Message },
            QuotaExceeded error => record with { State = RestoreOperationState.Failed, ErrorCode = error.Code, ErrorMessage = error.Message },
            Unavailable error => record with { State = RestoreOperationState.Failed, ErrorCode = error.Code, ErrorMessage = error.Message },
            null => record with { State = RestoreOperationState.Failed, ErrorCode = "interrupted", ErrorMessage = "the process stopped while the restore was running" },
            BackupElsewhere => record with { State = RestoreOperationState.Failed, ErrorCode = "restore_failed", ErrorMessage = "the restore could not be completed" },
        };
        await SettleAsync(record, settled with { FinishedAt = time.GetUtcNow() }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SettleAsync(RestoreOperationRecord record, RestoreOperationRecord settled, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (await SettleOnceAsync(record, settled, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                logger.ZLogWarning($"Restore operation {record.Id:@OperationId} settle failed, attempt {attempt}");
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.ZLogWarning(error, $"Restore operation {record.Id:@OperationId} settle failed, attempt {attempt}");
            }

            var delay = TimeSpan.FromTicks((long)Math.Min(TimeSpan.FromMilliseconds(250).Ticks * Math.Pow(2, attempt - 1), TimeSpan.FromSeconds(5).Ticks));
            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<bool> SettleOnceAsync(RestoreOperationRecord record, RestoreOperationRecord settled, CancellationToken cancellationToken) =>
        await catalog.WriteAsync<CatalogCheck>(async transaction =>
        {
            if (await GetRestoreOperationRecordAsync(transaction, record.OrganizationId, record.SourceDatabaseId, record.Id, CancellationToken.None)
                .ConfigureAwait(false) is { State: RestoreOperationState.Running })
            {
                await PutRestoreOperationAsync(transaction, settled, CancellationToken.None).ConfigureAwait(false);
                await ReleaseLeaseAsync(transaction, record, CancellationToken.None).ConfigureAwait(false);
            }

            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false) is Done;

    private async Task SettleStoppedAsync(RestoreOperationRecord record)
    {
        var interrupted = record with
        {
            State = RestoreOperationState.Failed,
            ErrorCode = "interrupted",
            ErrorMessage = "the process stopped while the restore was running",
            FinishedAt = time.GetUtcNow(),
        };
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await SettleOnceAsync(record, interrupted, stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            logger.ZLogWarning(error, $"Restore operation {record.Id:@OperationId} settle on stop failed");
        }
    }

    public async ValueTask RecoverInterruptedRestoresAsync(CancellationToken cancellationToken)
    {
        var running = await catalog.ReadAsync<IReadOnlyList<RestoreOperationRecord>?>(
            async transaction => await SystemCatalog.ListAllAsync(transaction, RestoreCatalog.Kind, ManagementJsonContext.Default.RestoreOperationRecord, cancellationToken)
                .ConfigureAwait(false),
            static _ => null, cancellationToken).ConfigureAwait(false);
        foreach (var record in running ?? [])
        {
            if (record.State != RestoreOperationState.Running || record.Region != options.Region
                || record.Node is not null && record.Node != options.MachineId
                || record.CreatedAt >= _startedAt || _runningRestores.ContainsKey(record.Id))
            {
                continue;
            }

            try
            {
                await RecoverInterruptedAsync(record, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.ZLogWarning(error, $"Recovering interrupted restore operation {record.Id:@OperationId} failed; the next pass tries again");
            }
        }
    }

    private async ValueTask RecoverInterruptedAsync(RestoreOperationRecord record, CancellationToken cancellationToken)
    {
        var target = await FindStrandedTargetAsync(record, cancellationToken).ConfigureAwait(false);
        var message = target is { } found
            ? $"the process stopped while the restore was running; database {found.Name} ({found.Id}) may hold a partial or complete restore — check it and delete it if unwanted"
            : "the process stopped while the restore was running";
        await SettleAsync(record, record with
        {
            State = RestoreOperationState.Failed,
            ErrorCode = "interrupted",
            ErrorMessage = message,
            FinishedAt = time.GetUtcNow(),
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ManagedDatabase?> FindStrandedTargetAsync(RestoreOperationRecord record, CancellationToken cancellationToken)
    {
        var candidates = await catalog.ReadAsync<List<ManagedDatabase>?>(
            async transaction =>
            {
                if (record.TargetName is { } name)
                {
                    return await CatalogTables.DatabaseNamedAsync(transaction, record.ProjectId, name, cancellationToken).ConfigureAwait(false) is { } id
                        && await CatalogTables.DatabaseAsync(transaction, record.ProjectId, id, cancellationToken).ConfigureAwait(false) is { } named
                            ? [named]
                            : [];
                }

                var found = new List<ManagedDatabase>();
                string? after = null;
                do
                {
                    var page = await CatalogTables.DatabasesAsync(transaction, record.ProjectId, after, PageSize, cancellationToken).ConfigureAwait(false);
                    found.AddRange(page.Items);
                    after = page.Next;
                }
                while (after is not null);
                return found;
            },
            static _ => null, cancellationToken).ConfigureAwait(false) ?? [];

        return candidates.FirstOrDefault(database => database.ProjectId == record.ProjectId
            && database.BranchedFrom == record.SourceDatabaseId
            && database.CreatedAt >= record.CreatedAt
            && (record.TargetName is null || database.Name == record.TargetName));
    }

    public ValueTask<RestoreBackupResult> RewindPointInTimeAsync(
        string userId, string organizationId, TenantContext database, DateTimeOffset at, string destination, CancellationToken cancellationToken) =>
        RewindFromStagingAsync(userId, organizationId, database, at, destination, cancellationToken);

    private async ValueTask<RestoreBackupResult> RewindFromStagingAsync(
        string userId, string organizationId, TenantContext database, DateTimeOffset at, string destination, CancellationToken cancellationToken)
    {
        if (PlatformDatabases.IsPlatform(database.OrganizationId))
        {
            return ErasureFenced();
        }

        var owner = DatabaseOwner.Of(database);
        var found = await catalog.ReadAsync<GetDatabaseResult>(
            async transaction => await EngineManagementService.GetDatabaseAsync(transaction, userId, organizationId, owner.ProjectId, owner.DatabaseId, cancellationToken)
                .ConfigureAwait(false) is { } database ? database : new NotFound(),
            static error => error, cancellationToken).ConfigureAwait(false);
        if (found is not ManagedDatabase source)
        {
            return found switch
            {
                NotFound error => error,
                Unavailable error => error,
                ManagedDatabase => throw new UnreachableException(),
            };
        }

        if (source.Home(options.Region) != options.Region)
        {
            return new BackupElsewhere(source.Home(options.Region), source);
        }

        var staging = database with { DatabaseId = EntityIds.New().ToString() };
        try
        {
            return await pointInTime.RestoreAsync(database, at, staging, cancellationToken, destination).ConfigureAwait(false) switch
            {
                Done => await rewind.RewindAsync(database, staging, cancellationToken).ConfigureAwait(false) ? source : new NotFound(),
                NotFound error => error,
                Conflict error => error,
                Validation error => error,
                Unavailable error => error,
            };
        }
        finally
        {
            await engine.DeleteDatabaseAsync(staging, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static Conflict ErasureFenced() => new("platform database historical recovery is fenced by erasure requirements", "recovery_erasure_fenced");

    private async ValueTask<RestoreBackupResult> CopyRestoredAsync(
        string userId, string organizationId, DatabaseOwner owner, string sourceName, string storageId, string? name, CancellationToken cancellationToken, long? ttlSeconds = null)
    {
        if (name is not null)
        {
            return Restored(await management.CopyDatabaseAsync(userId, organizationId, owner.ProjectId, storageId, name, owner.DatabaseId, cancellationToken, ttlSeconds).ConfigureAwait(false));
        }

        for (var attempt = 1; attempt <= MaxRestoreNameAttempts; attempt++)
        {
            var suffix = attempt == 1 ? "-restore" : "-restore-" + attempt.ToString(CultureInfo.InvariantCulture);
            var candidate = sourceName[..Math.Min(sourceName.Length, MaxDatabaseNameLength - suffix.Length)] + suffix;
            var result = await management.CopyDatabaseAsync(userId, organizationId, owner.ProjectId, storageId, candidate, owner.DatabaseId, cancellationToken, ttlSeconds).ConfigureAwait(false);
            if (result is not Conflict taken || taken != ManagementErrors.NameTaken)
            {
                return Restored(result);
            }
        }

        return ManagementErrors.NameTaken;
    }

    private static RestoreBackupResult Restored(CopyDatabaseResult copied) => copied switch
    {
        ManagedDatabase database => database,
        Conflict error => error,
        Forbidden error => error,
        NotFound => new NotFound(),
        QuotaExceeded error => error,
        Unavailable error => error,
    };


    private static async ValueTask ReleaseLeaseAsync(IStorageTransaction transaction, RestoreOperationRecord record, CancellationToken cancellationToken)
    {
        if (record.LeasedBackupId is { } backupId)
        {
            await transaction.DeleteAsync(BackupCatalog.Leases(record.OrganizationId), BackupCatalog.LeaseKey(backupId, record.Id), cancellationToken).ConfigureAwait(false);
        }

        if (record.LeasedDatabaseId is { } databaseId && record.LeasedGeneration is { } generation)
        {
            await transaction.DeleteAsync(
                BackupCatalog.GenerationLeases(record.OrganizationId), BackupCatalog.GenerationLeaseKey(databaseId, generation, record.Id), cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask PutLeaseAsync(IStorageTransaction transaction, string organizationId, string backupId, string operationId, CancellationToken cancellationToken) =>
        PutAsync(
            transaction, BackupCatalog.Leases(organizationId), BackupCatalog.LeaseKey(backupId, operationId),
            new BackupLease(backupId, operationId, time.GetUtcNow() + RestoreLeaseExpiry),
            ManagementJsonContext.Default.BackupLease, cancellationToken);

    private ValueTask PutGenerationLeaseAsync(IStorageTransaction transaction, string organizationId, string databaseId, Ulid generation, string operationId, CancellationToken cancellationToken) =>
        PutAsync(
            transaction, BackupCatalog.GenerationLeases(organizationId), BackupCatalog.GenerationLeaseKey(databaseId, generation, operationId),
            new RestoreGenerationLease(databaseId, generation, operationId, time.GetUtcNow() + RestoreLeaseExpiry),
            ManagementJsonContext.Default.RestoreGenerationLease, cancellationToken);

    private async ValueTask<bool> LeaseActiveAsync(IStorageTransaction transaction, BackupRecord record, CancellationToken cancellationToken)
    {
        var leases = await SystemCatalog.ListAllAsync(
            transaction, BackupCatalog.Leases(record.OrganizationId), ManagementJsonContext.Default.BackupLease, cancellationToken).ConfigureAwait(false);
        foreach (var lease in leases)
        {
            if (lease.BackupId == record.Id
                && lease.Until > time.GetUtcNow()
                && await GetAsync(transaction, RestoreCatalog.Kind, lease.OperationId, ManagementJsonContext.Default.RestoreOperationRecord, cancellationToken)
                    .ConfigureAwait(false) is { State: RestoreOperationState.Running })
            {
                return true;
            }
        }

        return false;
    }

    private async Task HeartbeatLeaseAsync(RestoreOperationRecord record, CancellationToken stopping)
    {
        var backupId = record.LeasedBackupId;
        var leasedDatabaseId = record.LeasedDatabaseId;
        if (backupId is null && leasedDatabaseId is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(RestoreLeaseHeartbeat, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                _ = await catalog.WriteAsync<CatalogCheck>(async transaction =>
                {
                    if (await GetRestoreOperationRecordAsync(transaction, record.OrganizationId, record.SourceDatabaseId, record.Id, CancellationToken.None)
                        .ConfigureAwait(false) is { State: RestoreOperationState.Running })
                    {
                        if (backupId is not null)
                        {
                            await PutLeaseAsync(transaction, record.OrganizationId, backupId, record.Id, CancellationToken.None).ConfigureAwait(false);
                        }

                        if (leasedDatabaseId is not null && record.LeasedGeneration is { } generation)
                        {
                            await PutGenerationLeaseAsync(transaction, record.OrganizationId, leasedDatabaseId, generation, record.Id, CancellationToken.None).ConfigureAwait(false);
                        }
                    }

                    return new Done();
                }, static error => error, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

}
