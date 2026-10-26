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
using TerraBase.Features.Platform;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Backups;

/// <summary>
/// Backups as a state machine persisted in the catalog (<see cref="BackupState"/>, <see cref="BackupRecord"/>). Every
/// transition re-reads the record and the database inside its write transaction, which the engine serializes, so a
/// concurrent delete, retention or policy change is always seen. Files are only ever removed by the storage sweep
/// (<see cref="StorageSweep"/>), whose entry is written in the same transaction that makes a snapshot unreferenced.
/// </summary>
/// <remarks>
/// <see cref="BackedUpDatabases"/> lists every registered database (each is backed up under its effective policy, the
/// default until one is saved), so a pass reads one flat list instead of walking organizations and projects. One pass:
/// one page of the sweep, then retention and turn selection a page of databases at a time, then at most
/// <see cref="MaxScheduledBackupsPerPass"/> scheduled backups in turn order.
/// </remarks>
internal sealed class EngineManagedBackupService(
    IStorageEngine engine, SystemCatalog catalog, IManagementService management, IPointInTimeRestore pointInTime, TimeProvider time,
    ILogger<EngineManagedBackupService> logger, TerraBaseOptions options)
    : IManagedBackupService
{
    /// <summary>Catalog collection: database id → <see cref="DatabaseOwner"/>, deleted with the database.</summary>
    public const string BackedUpDatabases = "backed_up_databases";

    private const string Manual = "manual";
    private const string Scheduled = "scheduled";
    private const string CurrentPolicy = "current";
    private const string LastSchedule = "last";
    private const string BackupSequence = "backup_sequence";
    private const int MaxDatabaseNameLength = 63;
    private const int MaxRestoreNameAttempts = 100;

    // Bounds the snapshot copies one pass takes; databases still due get theirs on the next pass.
    private const int MaxScheduledBackupsPerPass = 16;

    // Bounds what one catalog read or write materializes (databases, list entries, sweep entries).
    private const int PageSize = 100;

    // Catalog documents are small; this only bounds a page if one ever is not.
    private const long MaxPageBytes = 1024 * 1024;

    private static readonly JsonTypeInfo<string> Id = TerraBaseJsonContext.Default.String;

    // The backups this process is taking, from before their pending record commits until their attempt has ended. One
    // process owns the data directory, so a pending or snapshotting backup not in here was abandoned (its process
    // stopped mid-way, or settling its failure failed): the sweep expires it.
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    // Where the next pass resumes the sweep; passes run one at a time (BackupScheduler).
    private string? _sweepCursor;

    public static BackupPolicy DefaultPolicy { get; } = new("0 2 * * *", 7, false, null);

    public ValueTask<ListBackupsResult> ListBackupsAsync(
        string organizationId, string databaseId, string? after, int limit, CancellationToken cancellationToken) =>
        catalog.ReadAsync<ListBackupsResult>(async transaction =>
        {
            var page = await transaction.ListAsync(BackupCatalog.List(organizationId, databaseId), after, limit, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            var backups = new List<DatabaseBackup>(page.Documents.Count);
            foreach (var entry in page.Documents)
            {
                // Listed and unlisted in the same transactions as the record changes state: always registered.
                var record = await GetRecordAsync(transaction, organizationId, JsonSerializer.Deserialize(entry.Json.Span, Id)!, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("the backup list names a missing backup");
                backups.Add(record.ToBackup());
            }

            return new ManagementPage<DatabaseBackup>(backups, page.HasMore ? page.Documents[^1].Id : null);
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
        await catalog.ReadAsync<GetBackupPolicyResult>(
            async transaction => await GetPolicyAsync(transaction, databaseId, cancellationToken).ConfigureAwait(false), static error => error, cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<SetBackupPolicyResult> SetPolicyAsync(
        string userId, string organizationId, TenantContext database, string? fullBackupCron, int retentionDays, bool disabled, CancellationToken cancellationToken)
    {
        var policy = new BackupPolicy(fullBackupCron, retentionDays, disabled, time.GetUtcNow());
        var owner = DatabaseOwner.Of(database);
        return await catalog.WriteAsync<SetBackupPolicyResult>(async transaction =>
        {
            if (await EngineManagementService.GetDatabaseAsync(transaction, userId, organizationId, owner.ProjectId, owner.DatabaseId, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            // A backup policy is project state of the organization: a viewer cannot set it, and setting it changes the organization.
            var access = await EngineManagementService.WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not OrganizationMember)
            {
                return access switch
                {
                    Forbidden => ManagementErrors.RoleTooLow,
                    NotFound => new NotFound(),
                    OrganizationMember => throw new UnreachableException(),
                };
            }

            await EngineManagementService.TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, BackupCatalog.Policy(owner.DatabaseId), CurrentPolicy, policy, TerraBaseJsonContext.Default.BackupPolicy, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, BackedUpDatabases, owner.DatabaseId, owner, TerraBaseJsonContext.Default.DatabaseOwner, cancellationToken).ConfigureAwait(false);
            return policy;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    /// <remarks>
    /// ponytail: a backup that expires while it is being restored can have its snapshot swept mid-copy, which fails the
    /// restore cleanly; hold a restore lease on the record if restores of nearly expired backups must always succeed.
    /// </remarks>
    public async ValueTask<RestoreBackupResult> RestoreAsync(
        string userId, string organizationId, TenantContext database, string backupId, string? name, CancellationToken cancellationToken)
    {
        if (PlatformDatabases.IsPlatform(database.OrganizationId))
        {
            return ErasureFenced();
        }

        var owner = DatabaseOwner.Of(database);
        // The snapshot is an engine database of the backed-up database's project, which the restore copies within.
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
            RestoreSource source when source.Region != options.Region => new BackupElsewhere(source.Region!),
            RestoreSource source when !source.Database.PlacedIn(options.Region) => new Conflict("the snapshot's region is outside the database's current placement", "backup_region_not_placed"),
            RestoreSource source => await CopyRestoredAsync(userId, organizationId, owner, source.Database.Name, source.StorageId, name, cancellationToken).ConfigureAwait(false),
            NotFound error => error,
            Unavailable error => error,
        };
    }

    /// <remarks>
    /// ponytail: the restored state is staged as an engine database of the project and copied like a backup snapshot; a
    /// process that stops between the two leaves the staging file, which startup reports as unknown and keeps.
    /// </remarks>
    public async ValueTask<RestoreBackupResult> RestorePointInTimeAsync(
        string userId, string organizationId, TenantContext database, DateTimeOffset at, string? name, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform)
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
            return new BackupElsewhere(source.Home(options.Region));
        }

        var staging = database with { DatabaseId = EntityIds.New().ToString() };
        try
        {
            return await pointInTime.RestoreAsync(database, at, staging, cancellationToken, destination).ConfigureAwait(false) switch
            {
                Done => await CopyRestoredAsync(userId, organizationId, owner, source.Name, staging.DatabaseId!, name, cancellationToken).ConfigureAwait(false),
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

    // Receipt absence cannot prove safety: erasure may be pending or its SYSTEM receipt not yet replicated.
    private static Conflict ErasureFenced() => new("platform database historical recovery is fenced by erasure requirements", "recovery_erasure_fenced");

    // Copies the engine database storageId of the project into a new database named name, or <source>-restore, -restore-2, ….
    private async ValueTask<RestoreBackupResult> CopyRestoredAsync(
        string userId, string organizationId, DatabaseOwner owner, string sourceName, string storageId, string? name, CancellationToken cancellationToken)
    {
        if (name is not null)
        {
            return Restored(await management.CopyDatabaseAsync(userId, organizationId, owner.ProjectId, storageId, name, owner.DatabaseId, cancellationToken).ConfigureAwait(false));
        }

        // A taken name fails on the registration precheck, before any copy is made.
        for (var attempt = 1; attempt <= MaxRestoreNameAttempts; attempt++)
        {
            var suffix = attempt == 1 ? "-restore" : "-restore-" + attempt.ToString(CultureInfo.InvariantCulture);
            var candidate = sourceName[..Math.Min(sourceName.Length, MaxDatabaseNameLength - suffix.Length)] + suffix;
            var result = await management.CopyDatabaseAsync(userId, organizationId, owner.ProjectId, storageId, candidate, owner.DatabaseId, cancellationToken).ConfigureAwait(false);
            if (result is not Conflict taken || taken != ManagementErrors.NameTaken)
            {
                return Restored(result);
            }
        }

        return ManagementErrors.NameTaken;
    }

    // Preserve copied-database failures, including storage admission, on the restore route.
    private static RestoreBackupResult Restored(CopyDatabaseResult copied) => copied switch
    {
        ManagedDatabase database => database,
        Conflict error => error,
        Forbidden => ManagementErrors.RoleTooLow,
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

        if (await SweepAsync(cancellationToken).ConfigureAwait(false) is Unavailable)
        {
            return;
        }

        // Retention for every database, a page at a time, keeping only the best candidates in turn order.
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

        // The pass's backups are recorded pending together, then snapshotted one at a time.
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
                    // A scheduled attempt records its own outcome; a database deleted meanwhile has nothing to report.
                    _ = await SnapshotAsync(userId: null, record, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Already recorded as a failed attempt, or left to a later sweep; the next database still gets its turn.
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

    // pending → snapshotting → registered, or → expiring on any failure. Each step re-reads the record and the
    // database in its transaction. A user's backup re-checks their membership; a scheduled one (no user) only that the
    // database still exists. Every scheduled attempt ends in exactly one schedule update: success or failure.
    private async ValueTask<CreateBackupResult> SnapshotAsync(string? userId, BackupRecord record, CancellationToken cancellationToken)
    {
        SnapshotSourceResult found;
        try
        {
            found = await catalog.WriteAsync<SnapshotSourceResult>(async transaction =>
            {
                if (await GetRecordAsync(transaction, record.OrganizationId, record.Id, cancellationToken).ConfigureAwait(false) is not { State: BackupState.Pending } current)
                {
                    // Expired meanwhile (its database was deleted).
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
            await FailAsync(record).ConfigureAwait(false);
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
            // False: the source's files are gone although the catalog still names it. That is a failed attempt too.
            // The snapshot is a copy within the source's organization, next to it in its project.
            var snapshot = record.Storage(source.Plan);
            if (!await engine.CopyDatabaseAsync(source, snapshot, cancellationToken).ConfigureAwait(false))
            {
                await FailAsync(record).ConfigureAwait(false);
                return new NotFound();
            }

            var size = await engine.GetDatabaseSizeAsync(snapshot, cancellationToken).ConfigureAwait(false) ?? 0;
            registered = await catalog.WriteAsync<RegisteredSnapshotResult>(
                async transaction => await RegisterAsync(transaction, userId, record, size, cancellationToken).ConfigureAwait(false) is { } completed ? completed : new NotFound(),
                static error => error, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await FailAsync(record).ConfigureAwait(false);
            throw;
        }

        if (registered is Unavailable unavailable)
        {
            return unavailable;
        }

        if (registered is not BackupRecord ready)
        {
            // The database went away during the copy: the snapshot was expired with it.
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

        // The list key comes from the organization's sequence, incremented in this serialized transaction, so the list
        // is in registration order. The retention index orders the same backups by CompletedAt, so retention finds the
        // oldest first whatever order the clock gave them.
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
            await PutAsync(transaction, BackupCatalog.Schedule(done.DatabaseId), LastSchedule, new BackupSchedule(now, null), TerraBaseJsonContext.Default.BackupSchedule, cancellationToken)
                .ConfigureAwait(false);
        }

        return done;
    }

    // A failed attempt: the record expires (its sweep entry is already in place), a scheduled attempt on a database that
    // still exists records the failure, and the sweep is tried right away. Not cancellable: it settles what was started.
    private async ValueTask FailAsync(BackupRecord record)
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
                    var failure = (schedule ?? new BackupSchedule(null, null)) with { LastFailedAt = time.GetUtcNow() };
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
            // The record stays pending or snapshotting; once the attempt has ended, a later pass's sweep expires it.
            logger.ZLogWarning(error, $"Recording the failure of backup {record.Id:@BackupId} failed; a later storage sweep settles it");
            return;
        }

        _ = await SweepBackupAsync(record.OrganizationId, record.Id).ConfigureAwait(false);
    }

    // Retention, decided and applied in one write transaction that re-reads the policy, so a policy saved meanwhile is
    // the one applied. A read first checks whether anything can have expired, so most passes write nothing. Returns the
    // database's turn when its scheduled backup is due: the cron occurrence it is due since or, when a scheduled attempt
    // failed after that, the failed attempt.
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
                foreach (var entry in page.Documents)
                {
                    // The index is in completion order, so the first unexpired backup ends the expired ones.
                    var record = await GetRecordAsync(transaction, owner.OrganizationId, JsonSerializer.Deserialize(entry.Json.Span, Id)!, cancellationToken).ConfigureAwait(false);
                    if (record is not { State: BackupState.Registered, CompletedAt: { } at } || at >= cutoff)
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

    // One page of the storage sweep, from where the last pass stopped.
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

    // Expires one page of a deleted database's registered backups; the entry goes once none are left.
    private async ValueTask<CatalogCheck> ExpireDatabaseBackupsAsync(SweepEntry entry, CancellationToken cancellationToken)
    {
        var expiring = await catalog.WriteAsync<CatalogResult<List<BackupRecord>>>(async transaction =>
        {
            var page = await transaction.ListAsync(BackupCatalog.List(entry.OrganizationId, entry.Id), afterId: null, PageSize, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            var expired = new List<BackupRecord>(page.Documents.Count);
            foreach (var listed in page.Documents)
            {
                if (await GetRecordAsync(transaction, entry.OrganizationId, JsonSerializer.Deserialize(listed.Json.Span, Id)!, cancellationToken).ConfigureAwait(false) is { } record)
                {
                    await ExpireAsync(transaction, record, cancellationToken).ConfigureAwait(false);
                    expired.Add(record);
                }
            }

            if (!page.HasMore)
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

    // Drives one backup to deleted when the catalog says its snapshot is unreferenced: expiring, or pending or
    // snapshotting with no attempt in flight (abandoned). An attempt in flight is left alone. The record and its entry go
    // only after the files are confirmed gone.
    private async ValueTask<CatalogCheck> SweepBackupAsync(string organizationId, string backupId)
    {
        var entry = new SweepEntry(StorageSweep.Backup, organizationId, backupId);
        var selected = await catalog.WriteAsync<CatalogResult<SweepBackupState>>(async transaction =>
        {
            var plan = await EngineManagementService.PlanUncheckedAsync(transaction, organizationId, CancellationToken.None).ConfigureAwait(false);
            var record = await GetRecordAsync(transaction, organizationId, backupId, CancellationToken.None).ConfigureAwait(false);
            // Another region's process owns its in-flight copy and its files. Its sweep must finish them.
            if (record?.Region is { } region && region != options.Region)
            {
                return new SweepBackupState(null, plan);
            }

            switch (record?.State)
            {
                case null or BackupState.Registered:
                    // Nothing (left) to sweep: deleted already, or registered after all.
                    await StorageSweep.RemoveAsync(transaction, entry, CancellationToken.None).ConfigureAwait(false);
                    return new SweepBackupState(null, plan);
                case BackupState.Pending or BackupState.Snapshotting when _inFlight.ContainsKey(record.Id):
                    return new SweepBackupState(null, plan);
                case BackupState.Pending or BackupState.Snapshotting:
                    // Its exact failure time is unknown; use the attempt's start, so recovery never overwrites a
                    // newer scheduled success or failure. Persist the result with the recovery transition.
                    if (record.Trigger == Scheduled
                        && await EngineManagementService.DatabaseTenantUncheckedAsync(transaction, organizationId, record.ProjectId, record.DatabaseId, CancellationToken.None)
                            .ConfigureAwait(false) is not null)
                    {
                        var schedule = await GetAsync(transaction, BackupCatalog.Schedule(record.DatabaseId), LastSchedule, TerraBaseJsonContext.Default.BackupSchedule, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!(schedule?.LastSucceededAt >= record.StartedAt) && !(schedule?.LastFailedAt >= record.StartedAt))
                        {
                            var failure = (schedule ?? new BackupSchedule(null, null)) with { LastFailedAt = record.StartedAt };
                            await PutAsync(transaction, BackupCatalog.Schedule(record.DatabaseId), LastSchedule, failure, TerraBaseJsonContext.Default.BackupSchedule, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    }

                    record = record with { State = BackupState.Expiring };
                    await PutRecordAsync(transaction, record, CancellationToken.None).ConfigureAwait(false);
                    return new SweepBackupState(record, plan);
                default:
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

        if (record is null || !await StorageSweep.DeleteFilesAsync(engine, record.Storage(plan), logger).ConfigureAwait(false))
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

    // → expiring: unlisted (and out of the retention index) if it was listed, and owed to the sweep (the entry may
    // already exist).
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

    // pending, with its sweep entry, so whatever happens after this commit the sweep can find the snapshot.
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
        return first.Documents.Count == 0
            ? null
            : await GetRecordAsync(transaction, owner.OrganizationId, JsonSerializer.Deserialize(first.Documents[0].Json.Span, Id)!, cancellationToken).ConfigureAwait(false);
    }

    // The backed-up database's tenant: through the user's membership for a manual backup, for no user for a scheduled one.
    private static ValueTask<TenantContext?> GetDatabaseAsync(IStorageTransaction transaction, string? userId, BackupRecord record, CancellationToken cancellationToken) =>
        userId is null
            ? EngineManagementService.DatabaseTenantUncheckedAsync(transaction, record.OrganizationId, record.ProjectId, record.DatabaseId, cancellationToken)
            : EngineManagementService.MemberDatabaseTenantAsync(transaction, userId, record.OrganizationId, record.ProjectId, record.DatabaseId, cancellationToken);

    private static ValueTask<BackupRecord?> GetRecordAsync(IStorageTransaction transaction, string organizationId, string backupId, CancellationToken cancellationToken) =>
        GetAsync(transaction, BackupCatalog.Records(organizationId), backupId, TerraBaseJsonContext.Default.BackupRecord, cancellationToken);

    private static ValueTask PutRecordAsync(IStorageTransaction transaction, BackupRecord record, CancellationToken cancellationToken) =>
        PutAsync(transaction, BackupCatalog.Records(record.OrganizationId), record.Id, record, TerraBaseJsonContext.Default.BackupRecord, cancellationToken);

    private static SweepEntry SweepOf(BackupRecord record) => new(StorageSweep.Backup, record.OrganizationId, record.Id);

    // Completion ticks first, then the list key for uniqueness: oldest completion first.
    private static string RetentionKey(DateTimeOffset completedAt, string order) => EngineManagementService.TicksKey(completedAt) + order;

    // The first cron occurrence after the later of the last scheduled success and the policy save (the database's
    // creation for the default policy, which is never saved), when it has passed. The API returns the same policy. The
    // last success is kept apart from the backups, so retention deleting it does not make it due again.
    private static DateTimeOffset? DueSince(BackupPolicy policy, BackupSchedule? schedule, DateTimeOffset createdAt, DateTimeOffset now)
    {
        if (policy is not { Disabled: false, FullBackupCron: { } expression }
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

    // A pending backup recorded, or why not.
    private readonly union RecordPendingResult(Done, NotFound, Unavailable);

    private readonly union CatalogResult<T>(T, Unavailable);

    private readonly union SnapshotSourceResult(TenantContext, NotFound, Unavailable);

    private readonly union RegisteredSnapshotResult(BackupRecord, NotFound, Unavailable);

    private sealed record RetentionState(ManagedDatabase? Database, BackupPolicy Policy, BackupSchedule? Schedule, BackupRecord? Oldest);

    private sealed record BackupTurn(DateTimeOffset Turn, bool Failed);

    private readonly union BackupTurnResult(BackupTurn, NotFound, Unavailable);

    private sealed record SweepBackupState(BackupRecord? Record, PlanTier Plan);

    // The name of the database a backup restores and the engine database holding the backup.
    private sealed record RestoreSource(ManagedDatabase Database, string StorageId, string? Region);

    // What a restore copies, or why there is nothing to restore.
    private readonly union RestoreSourceResult(RestoreSource, NotFound, Unavailable);
}
