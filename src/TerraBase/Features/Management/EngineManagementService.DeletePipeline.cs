using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{

    public async ValueTask<DeleteDatabaseResult> DeleteDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken)
    {
        var target = await ReadAsync<DeletableDatabaseResult>(
            async transaction => await DeletableAsync(transaction, userId, organizationId, projectId, databaseId, options.Region, cancellationToken).ConfigureAwait(false) switch
            {
                DeletableDatabase deletable => deletable,
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
            },
            static error => error, cancellationToken).ConfigureAwait(false);
        if (target is not DeletableDatabase deletable)
        {
            return target switch
            {
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
                Unavailable error => error,
                DeletableDatabase => throw new UnreachableException(),
            };
        }

        ManagedDatabase? committed = null;
        Conflict? moveRefusal = null;
        bool Unregistered(ManagedDatabase record)
        {
            committed = record;
            return true;
        }

        bool MovedDuringCommit(Conflict error)
        {
            moveRefusal = error;
            return false;
        }

        return await DeleteRecordedAsync(
            deletable.Storage,
            async (transaction, token) =>
                await UnregisterDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, options.Region, token).ConfigureAwait(false) switch
                {
                    DeletableDatabase unregistered => Unregistered(unregistered.Record),
                    Conflict error => MovedDuringCommit(error),
                    _ => false,
                },
            cancellationToken).ConfigureAwait(false) switch
        {
            bool deleted => moveRefusal is { } refused ? refused : !deleted ? ManagementErrors.NotFound
                : await ForgetReplicaRegionsAsync(deletable.Storage, committed ?? deletable.Record, CancellationToken.None).ConfigureAwait(false),
            Unavailable refused => refused,
        };
    }

    private async ValueTask<DeleteDatabaseResult> ForgetReplicaRegionsAsync(TenantContext storage, ManagedDatabase record, CancellationToken cancellationToken)
    {
        try
        {
            var pushes = (await regionPeers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false))
                .Where(peer => record.AllRegions || record.Replicas.Contains(peer.Region, StringComparer.Ordinal))
                .Select(peer => remote.ForgetAsync(peer.Region, peer.Address, storage, cancellationToken))
                .ToArray();
            if (pushes.Length > 0)
            {
                await Task.WhenAll(pushes).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.ZLogWarning(error, $"Telling replica regions to forget deleted database {storage.DatabaseId:@Database} failed ({error.GetType().Name:@Error}); their own sweeps forget it");
        }

        return await shredding.Value.DestroyAsync(storage, DateTimeOffset.MaxValue, cancellationToken).ConfigureAwait(false) ? new Done()
            : new Unavailable("database deletion committed; key destruction is pending and will retry");
    }

    private Done Shredded(TenantContext storage)
    {
        shredding.Value.Shred(storage);
        return new Done();
    }

    private async ValueTask<RecordedDeletion> DeleteRecordedAsync(
        TenantContext storage, Func<IStorageTransaction, CancellationToken, ValueTask<bool>> unregister, CancellationToken cancellationToken)
    {
        var recording = false;
        RecordedDeletion decision = false;
        bool recorded;
        try
        {
            recorded = await engine.RecordDatabaseDeleteIntentAsync(storage, async token =>
            {
                recording = true;
                checkpoint("delete.recording", storage);
                var result = await WriteAsync<RecordedDeletion>(async transaction => await unregister(transaction, token).ConfigureAwait(false),
                    static error => error, token).ConfigureAwait(false);
                decision = result;
                return result switch
                {
                    bool accepted => accepted,
                    Unavailable => false,
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (recording)
            {
                await ReleaseUnrecordedIntentAsync(storage).ConfigureAwait(false);
            }

            throw;
        }
        if (!recorded)
        {
            return decision;
        }

        checkpoint("delete.intent", storage);
        var cleanup = await FinishDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false);
        if (cleanup is Unavailable error)
        {
            return error;
        }

        checkpoint("delete.finalized", storage);
        return true;
    }

    private readonly union RecordedDeletion(bool, Unavailable);

    private readonly union DeletionPlacement(Done, Conflict);

    private static DeletionPlacement CheckDeletionPlacement(ManagedDatabase database, string region) =>
        database.Home(region) != region ? ManagementErrors.WrongRegion : new Done();

    private static async ValueTask<DeletableDatabaseCheck> DeletableAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, string region, CancellationToken cancellationToken)
    {
        var project = await AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        if (project is not TenantContext tenant)
        {
            return project switch
            {
                NotFound error => error,
                Forbidden error => error,
                TenantContext => throw new UnreachableException(),
            };
        }

        if (PlatformDatabases.IsPlatform(tenant.OrganizationId))
        {
            return ManagementErrors.PlatformDatabaseProtected;
        }

        if (await CatalogTables.DatabaseAsync(transaction, projectId, databaseId, cancellationToken).ConfigureAwait(false) is not { } database)
        {
            return ManagementErrors.NotFound;
        }

        if (CheckDeletionPlacement(database, region) is Conflict refused)
        {
            return refused;
        }

        if (await HomeMovePendingAsync(transaction, databaseId, cancellationToken).ConfigureAwait(false))
        {
            return MoveInProgress;
        }

        return new DeletableDatabase(tenant with { DatabaseId = database.Id }, database);
    }

    private async ValueTask<UnregisterResult> UnregisterDatabaseAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, string region, CancellationToken cancellationToken)
    {
        var deletable = await DeletableAsync(transaction, userId, organizationId, projectId, databaseId, region, cancellationToken).ConfigureAwait(false) switch
        {
            DeletableDatabase found => (UnregisterResult)found,
            NotFound error => error,
            Forbidden error => error,
            Conflict error => error,
        };
        if (deletable is DeletableDatabase record)
        {
            _ = await UnregisterRecordAsync(transaction, record.Storage, record.Record, cancellationToken, customerDeletion: true).ConfigureAwait(false);
            return record;
        }

        return deletable;
    }

    private async ValueTask<TenantContext> UnregisterRecordAsync(
        IStorageTransaction transaction, TenantContext storage, ManagedDatabase database, CancellationToken cancellationToken, bool customerDeletion = false)
    {
        if (customerDeletion)
        {
            await RecordDatabasePurgeAsync(transaction, storage, database, cancellationToken).ConfigureAwait(false);
            await BackupDestinationHistory.CaptureAsync(transaction, storage, cancellationToken).ConfigureAwait(false);
        }
        _ = await CatalogTables.PutPendingCleanupAsync(transaction, storage, cancellationToken).ConfigureAwait(false);
        _ = await CatalogTables.DeleteDatabaseAsync(transaction, database.Id, cancellationToken).ConfigureAwait(false);
        await StorageSweep.AddAsync(transaction, new SweepEntry(StorageSweep.DatabaseBackups, storage.OrganizationId, database.Id), cancellationToken).ConfigureAwait(false);
        _ = await transaction.DeleteAsync(EngineManagedBackupService.BackedUpDatabases, database.Id, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, RetiredDatabases, database.Id, DatabaseOwner.Of(storage), ManagementJsonContext.Default.DatabaseOwner, cancellationToken)
            .ConfigureAwait(false);

        await transaction.DeleteKindsAsync(SystemCatalog.DatabaseState(database.Id), cancellationToken).ConfigureAwait(false);

        return storage;
    }


    private sealed record DeletableDatabase(TenantContext Storage, ManagedDatabase Record);

    private readonly union DeletableDatabaseCheck(DeletableDatabase, NotFound, Forbidden, Conflict);

    private readonly union DeletableDatabaseResult(DeletableDatabase, NotFound, Forbidden, Conflict, Unavailable);

    private readonly union UnregisterResult(DeletableDatabase, NotFound, Forbidden, Conflict);
}
