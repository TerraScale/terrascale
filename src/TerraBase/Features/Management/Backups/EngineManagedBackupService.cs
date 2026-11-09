using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cronos;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Backup;
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

internal sealed class EngineManagedBackupService(
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
    private const int MaxDatabaseNameLength = 63;
    private const int MaxRestoreNameAttempts = 100;

    private static readonly TimeSpan RestoreLeaseExpiry = TimeSpan.FromHours(1);

    private static readonly TimeSpan RestoreLeaseHeartbeat = TimeSpan.FromMinutes(5);

    private const int MaxScheduledBackupsPerPass = 16;

    private const int PageSize = 100;

    private const long MaxPageBytes = 1024 * 1024;

    private static readonly JsonTypeInfo<string> Id = TerraBaseJsonContext.Default.String;

    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    private string? _sweepCursor;

    private readonly ConcurrentDictionary<string, byte> _runningRestores = new();

    private readonly DateTimeOffset _startedAt = time.GetUtcNow();

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
            var schedule = await GetAsync(transaction, BackupCatalog.Schedule(databaseId), LastSchedule, TerraBaseJsonContext.Default.BackupSchedule, cancellationToken).ConfigureAwait(false);
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

            await PutAsync(transaction, BackupCatalog.Policy(owner.DatabaseId), CurrentPolicy, policy, TerraBaseJsonContext.Default.BackupPolicy, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, BackedUpDatabases, owner.DatabaseId, owner, TerraBaseJsonContext.Default.DatabaseOwner, cancellationToken).ConfigureAwait(false);
            return policy;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

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
        var record = await GetAsync(transaction, RestoreCatalog.Kind, operationId, TerraBaseJsonContext.Default.RestoreOperationRecord, cancellationToken).ConfigureAwait(false);
        return record is { OrganizationId: var organization, SourceDatabaseId: var source } && organization == organizationId && source == databaseId
            ? record
            : null;
    }

    private static ValueTask PutRestoreOperationAsync(IStorageTransaction transaction, RestoreOperationRecord record, CancellationToken cancellationToken) =>
        PutAsync(transaction, RestoreCatalog.Kind, record.Id, record, TerraBaseJsonContext.Default.RestoreOperationRecord, cancellationToken);

    private async ValueTask<StartRestoreResult> StartOperationAsync(
        string organizationId, DatabaseOwner owner, string kind, string? name,
        Func<CancellationToken, ValueTask<RestoreBackupResult>> copy, CancellationToken cancellationToken, string? leasedBackupId = null,
        string? leasedDatabaseId = null, Ulid? leasedGeneration = null)
    {
        var id = EntityIds.New(time).ToString();
        _ = _runningRestores.TryAdd(id, 0);
        var admitted = await catalog.WriteAsync<StartRestoreResult>(async transaction =>
        {
            var running = await SystemCatalog.ListAllAsync(transaction, RestoreCatalog.Kind, TerraBaseJsonContext.Default.RestoreOperationRecord, cancellationToken)
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
            async transaction => await SystemCatalog.ListAllAsync(transaction, RestoreCatalog.Kind, TerraBaseJsonContext.Default.RestoreOperationRecord, cancellationToken)
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
                async transaction => await ListAsync(transaction, BackedUpDatabases, after, PageSize, TerraBaseJsonContext.Default.DatabaseOwner, owner => owner.DatabaseId, cancellationToken)
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
                        var schedule = await GetAsync(transaction, BackupCatalog.Schedule(owner.DatabaseId), LastSchedule, TerraBaseJsonContext.Default.BackupSchedule, cancellationToken)
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
        await PutAsync(transaction, BackedUpDatabases, done.DatabaseId, new DatabaseOwner(done.OrganizationId, done.ProjectId, done.DatabaseId), TerraBaseJsonContext.Default.DatabaseOwner, cancellationToken)
            .ConfigureAwait(false);
        if (done.Trigger == Scheduled)
        {
            await PutAsync(transaction, BackupCatalog.Schedule(done.DatabaseId), LastSchedule, new BackupSchedule(now, null, null, null), TerraBaseJsonContext.Default.BackupSchedule, cancellationToken)
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
                    var schedule = await GetAsync(transaction, BackupCatalog.Schedule(record.DatabaseId), LastSchedule, TerraBaseJsonContext.Default.BackupSchedule, CancellationToken.None)
                        .ConfigureAwait(false);
                    var failure = (schedule ?? new BackupSchedule(null, null, null, null)) with { LastFailedAt = time.GetUtcNow(), LastErrorCode = errorCode, LastErrorMessage = errorMessage };
                    await PutAsync(transaction, BackupCatalog.Schedule(record.DatabaseId), LastSchedule, failure, TerraBaseJsonContext.Default.BackupSchedule, CancellationToken.None)
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
                await GetAsync(transaction, BackupCatalog.Schedule(owner.DatabaseId), LastSchedule, TerraBaseJsonContext.Default.BackupSchedule, cancellationToken).ConfigureAwait(false),
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
                        var schedule = await GetAsync(transaction, BackupCatalog.Schedule(record.DatabaseId), LastSchedule, TerraBaseJsonContext.Default.BackupSchedule, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!(schedule?.LastSucceededAt >= record.StartedAt) && !(schedule?.LastFailedAt >= record.StartedAt))
                        {
                            var failure = (schedule ?? new BackupSchedule(null, null, null, null)) with { LastFailedAt = record.StartedAt, LastErrorCode = "abandoned", LastErrorMessage = "The scheduled attempt did not finish; its process stopped mid-way" };
                            await PutAsync(transaction, BackupCatalog.Schedule(record.DatabaseId), LastSchedule, failure, TerraBaseJsonContext.Default.BackupSchedule, CancellationToken.None)
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
        if (!await shredding.Value.DestroyAsync(storage, CancellationToken.None).ConfigureAwait(false)
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
        GetAsync(transaction, BackupCatalog.Records(organizationId), backupId, TerraBaseJsonContext.Default.BackupRecord, cancellationToken);

    private static ValueTask PutRecordAsync(IStorageTransaction transaction, BackupRecord record, CancellationToken cancellationToken) =>
        PutAsync(transaction, BackupCatalog.Records(record.OrganizationId), record.Id, record, TerraBaseJsonContext.Default.BackupRecord, cancellationToken);

    private static SweepEntry SweepOf(BackupRecord record) => new(StorageSweep.Backup, record.OrganizationId, record.Id);

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
            TerraBaseJsonContext.Default.BackupLease, cancellationToken);

    private ValueTask PutGenerationLeaseAsync(IStorageTransaction transaction, string organizationId, string databaseId, Ulid generation, string operationId, CancellationToken cancellationToken) =>
        PutAsync(
            transaction, BackupCatalog.GenerationLeases(organizationId), BackupCatalog.GenerationLeaseKey(databaseId, generation, operationId),
            new RestoreGenerationLease(databaseId, generation, operationId, time.GetUtcNow() + RestoreLeaseExpiry),
            TerraBaseJsonContext.Default.RestoreGenerationLease, cancellationToken);

    private async ValueTask<bool> LeaseActiveAsync(IStorageTransaction transaction, BackupRecord record, CancellationToken cancellationToken)
    {
        var leases = await SystemCatalog.ListAllAsync(
            transaction, BackupCatalog.Leases(record.OrganizationId), TerraBaseJsonContext.Default.BackupLease, cancellationToken).ConfigureAwait(false);
        foreach (var lease in leases)
        {
            if (lease.BackupId == record.Id
                && lease.Until > time.GetUtcNow()
                && await GetAsync(transaction, RestoreCatalog.Kind, lease.OperationId, TerraBaseJsonContext.Default.RestoreOperationRecord, cancellationToken)
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
        await GetAsync(transaction, BackupCatalog.Policy(databaseId), CurrentPolicy, TerraBaseJsonContext.Default.BackupPolicy, cancellationToken).ConfigureAwait(false)
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
