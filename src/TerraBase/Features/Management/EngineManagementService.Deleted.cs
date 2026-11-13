using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Errors;
using TerraBase.Features.Qos;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private string? _deletedAfter;
    private long _nonwriterRetentionPasses;
    private async ValueTask<T> WriteRetentionAsync<T>(string database,
        Func<IStorageTransaction, ValueTask<T>> write, Func<Unavailable, T> unavailable, CancellationToken token)
    {
        using var lease = await retention.EnterAsync(database, token).ConfigureAwait(false);
        if (lease is null)
        {
            return unavailable(retention.Writer
                ? new Unavailable("copy not available on this node yet", "retained_copy_unavailable")
                : new Unavailable("this machine is not the current regional writer", "not_writer"));
        }
        return await WriteAsync<T>(transaction =>
        {
            lease.Fence(transaction);
            return write(transaction);
        }, unavailable, token).ConfigureAwait(false);
    }


    private async ValueTask<DeletedDatabaseRecord> RetainDeletedAsync(
        IStorageTransaction transaction, TenantContext storage, ManagedDatabase database, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var revisions = (IDatabaseRevisions)engine;
        _ = await revisions.DrainWritersAsync(storage, cancellationToken).ConfigureAwait(false);
        var position = await revisions.CurrentRevisionAsync(storage, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("the deleted database has no certified position");
        var record = new DeletedDatabaseRecord(storage, database, now, now.AddDays(7), position.ToString());
        await PutAsync(transaction, DeletedDatabases.Kind, database.Id, record, ManagementJsonContext.Default.DeletedDatabaseRecord, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, DeletedDatabases.Project(database.ProjectId), database.Id, record,
            ManagementJsonContext.Default.DeletedDatabaseRecord, cancellationToken).ConfigureAwait(false);
        _ = await CatalogTables.DeleteDatabaseAsync(transaction, database.Id, cancellationToken, retireIdentity: false).ConfigureAwait(false);
        ClearKeptNames(database.Id);
        return record;
    }

    public ValueTask<ListDeletedDatabasesResult> ListDeletedDatabasesAsync(
        string userId, string organizationId, string projectId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ListDeletedDatabasesResult>(async transaction =>
        {
            if (ProjectAccess.Current is { DatabaseName: not null }
                || await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null)
            {
                return ManagementErrors.NotFound;
            }

            var page = await SystemCatalog.ListAsync(transaction, DeletedDatabases.Project(projectId), after, limit,
                ManagementJsonContext.Default.DeletedDatabaseRecord, record => record.Database.Id, cancellationToken).ConfigureAwait(false);
            return new ManagementPage<DeletedDatabaseBody>([.. page.Items.Where(record => record.Storage.OrganizationId == organizationId).Select(record => record.Body)], page.Next);
        }, static error => error, cancellationToken);

    public async ValueTask<SetDatabaseRegionsResult> RestoreDeletedDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken)
    {
        if (ProjectAccess.Current is { DatabaseName: not null })
        {
            return ManagementErrors.NotFound;
        }
        var allowance = await AllowanceAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var entered = await quota.Value.EnterOrganizationsAsync([organizationId], cancellationToken).ConfigureAwait(false);
        if (entered is not StorageQuotaGroupLease lease)
        {
            return entered switch
            {
                Unavailable error => error,
                StorageQuotaGroupLease => throw new UnreachableException(),
            };
        }
        using var held = lease;
        var storage = await lease.CheckAsync(organizationId, 0, cancellationToken).ConfigureAwait(false);
        if (storage is not Done)
        {
            return storage switch
            {
                QuotaExceeded error => new Conflict(error.Message, "storage_quota"),
                Unavailable error => error,
                Done => throw new UnreachableException(),
            };
        }
        using var authority = await retention.EnterAsync(databaseId, cancellationToken).ConfigureAwait(false);
        if (authority is null)
        {
            return retention.Writer
                ? new Unavailable("copy not available on this node yet", "retained_copy_unavailable")
                : new Unavailable("this machine is not the current regional writer", "not_writer");
        }
        var prepared = await ReadAsync<SoftDeletionResult>(async transaction =>
        {
            var project = await AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (project is not TenantContext)
            {
                return project switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    TenantContext => throw new UnreachableException(),
                };
            }
            var retained = await GetAsync(transaction, DeletedDatabases.Kind, databaseId, ManagementJsonContext.Default.DeletedDatabaseRecord, cancellationToken).ConfigureAwait(false);
            return retained is not null && retained.Storage.OrganizationId == organizationId && retained.Database.ProjectId == projectId
                ? retained : ManagementErrors.NotFound;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (prepared is not DeletedDatabaseRecord verified)
        {
            return prepared switch
            {
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
                Unavailable error => error,
                DeletedDatabaseRecord => throw new UnreachableException(),
            };
        }
        if (await RetainedFilesUnavailableAsync(verified, cancellationToken).ConfigureAwait(false) is { } missing)
        {
            return missing;
        }
        return await WriteAsync<SetDatabaseRegionsResult>(async transaction =>
        {
            authority.Fence(transaction);
            if (ProjectAccess.Current is { DatabaseName: not null })
            {
                return ManagementErrors.NotFound;
            }

            var project = await AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (project is not TenantContext)
            {
                return project switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    TenantContext => throw new UnreachableException(),
                };
            }

            var record = await GetAsync(transaction, DeletedDatabases.Kind, databaseId, ManagementJsonContext.Default.DeletedDatabaseRecord, cancellationToken).ConfigureAwait(false);
            if (record is null || record.Storage.OrganizationId != organizationId || record.Database.ProjectId != projectId)
            {
                return ManagementErrors.NotFound;
            }

            if (await transaction.GetAsync(DatabasePurge.Kind, databaseId, cancellationToken).ConfigureAwait(false) is not null)
            {
                return new Conflict("permanent deletion has already begun", "database_purging");
            }

            if (CheckDeletionPlacement(record.Database, options.Region) is Conflict refused)
            {
                return refused;
            }

            var check = await CanAddDatabaseAsync(transaction, _ => ValueTask.FromResult<ProjectTenant>(project), record.Database.Name, cancellationToken, allowance)
                .ConfigureAwait(false);
            if (check is not TenantContext)
            {
                return check switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    Conflict error => error,
                    TenantContext => throw new UnreachableException(),
                };
            }

            if (record.DeletedAt != verified.DeletedAt || record.Revision != verified.Revision
                || !DatabasePurge.SameDatabase(record.Storage, verified.Storage) || !engine.HasDatabaseFiles(record.Storage, cancellationToken))
            {
                return new Unavailable("copy not available on this node yet", "retained_copy_unavailable");
            }

            var restored = record.Database with { ExpiresAt = null };
            await CatalogTables.PutDatabaseAsync(transaction, restored, cancellationToken).ConfigureAwait(false);
            await DeletedDatabases.RemoveAsync(transaction, record, cancellationToken).ConfigureAwait(false);
            return restored;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PurgeDatabaseResult> PurgeDeletedDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken)
    {
        try
        {
            return await PurgeDeletedCoreAsync(userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _purgeErrors.Add(1, new KeyValuePair<string, object?>("reason", "retained_purge"));
            logger.ZLogWarning(error, $"Permanent purge of retained database {databaseId:@Database} remains unverified");
            var job = await GetDatabasePurgeAsync(userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false);
            return job is DatabasePurgeBody ? job : new Unavailable("permanent purge could not be verified");
        }
    }

    private async ValueTask<PurgeDatabaseResult> PurgeDeletedCoreAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken)
    {
        using var authority = await retention.EnterAsync(databaseId, cancellationToken).ConfigureAwait(false);
        if (authority is null)
        {
            return new Unavailable("this machine is not the current regional writer", "not_writer");
        }
        var known = await GetDatabasePurgeAsync(userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false);
        if (known is DatabasePurgeBody existing)
        {
            if (existing.State == "complete")
            {
                return new NotFound("the database has been permanently purged", "database_gone");
            }
            authority.Dispose();
            await AdvancePurgeAsync(databaseId, cancellationToken).ConfigureAwait(false);
            return await GetDatabasePurgeAsync(userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false);
        }
        if (known is Forbidden forbidden)
        {
            return forbidden;
        }
        if (known is Unavailable unavailable)
        {
            return unavailable;
        }
        var prepared = await ReadAsync<SoftDeletionResult>(async transaction =>
        {
            if (ProjectAccess.Current is { DatabaseName: not null })
            {
                return ManagementErrors.NotFound;
            }
            var project = await AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (project is not TenantContext)
            {
                return project switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    TenantContext => throw new UnreachableException(),
                };
            }
            var record = await GetAsync(transaction, DeletedDatabases.Kind, databaseId, ManagementJsonContext.Default.DeletedDatabaseRecord, cancellationToken).ConfigureAwait(false);
            if (record is null || record.Storage.OrganizationId != organizationId || record.Database.ProjectId != projectId)
            {
                return ManagementErrors.NotFound;
            }
            if (CheckDeletionPlacement(record.Database, options.Region) is Conflict refused)
            {
                return refused;
            }
            return record;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (prepared is not DeletedDatabaseRecord retained)
        {
            return prepared switch
            {
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
                Unavailable error => error,
                DeletedDatabaseRecord => throw new UnreachableException(),
            };
        }
        if (await RetainedFilesUnavailableAsync(retained, cancellationToken).ConfigureAwait(false) is { } missing)
        {
            return missing;
        }
        var result = await DeleteRecordedAsync(retained.Storage, async (transaction, token) =>
        {
            authority.Fence(transaction);
            if (await AuthorizedProjectAsync(transaction, userId, organizationId, projectId, token).ConfigureAwait(false) is not TenantContext)
            {
                return false;
            }
            var current = await GetAsync(transaction, DeletedDatabases.Kind, databaseId, ManagementJsonContext.Default.DeletedDatabaseRecord, token).ConfigureAwait(false);
            if (current is null || current.DeletedAt != retained.DeletedAt || !DatabasePurge.SameDatabase(current.Storage, retained.Storage))
            {
                return false;
            }
            if (current.Revision != retained.Revision || !engine.HasDatabaseFiles(current.Storage, token))
            {
                return false;
            }
            _ = await UnregisterRecordAsync(transaction, current.Storage, current.Database, token, customerDeletion: true).ConfigureAwait(false);
            await DeletedDatabases.RemoveAsync(transaction, current, token).ConfigureAwait(false);
            return true;
        }, cancellationToken, authority).ConfigureAwait(false);
        if (result is true)
        {
            authority.Dispose();
            await AdvancePurgeAsync(databaseId, cancellationToken).ConfigureAwait(false);
            return await GetDatabasePurgeAsync(userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false);
        }
        var job = await GetDatabasePurgeAsync(userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false);
        if (job is DatabasePurgeBody)
        {
            return job;
        }
        return result switch
        {
            true => throw new UnreachableException(),
            false => new Conflict("the deleted database changed during purge", "database_purging"),
            Unavailable error => error,
        };
    }

    private async ValueTask PurgeDeletedDatabasesAsync(CancellationToken cancellationToken)
    {
        if (!retention.Writer)
        {
            Interlocked.Increment(ref _nonwriterRetentionPasses);
            return;
        }
        var page = await ReadAsync<ManagementPage<DeletedDatabaseRecord>?>(async transaction => await SystemCatalog.ListAsync(
            transaction, DeletedDatabases.Kind, _deletedAfter, CatalogPage, ManagementJsonContext.Default.DeletedDatabaseRecord,
            record => record.Database.Id, cancellationToken).ConfigureAwait(false), static _ => null, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return;
        }

        var waiting = 0L;
        foreach (var record in page.Items.Where(record => record.PurgeAt.AddHours(1) <= time.GetUtcNow() && record.Database.Home(options.Region) == options.Region))
        {
            try
            {
                if (!await ConfirmExpiryAsync(record, cancellationToken).ConfigureAwait(false))
                {
                    waiting++;
                    continue;
                }
                if (await PurgeDeletedRecordAsync(record, cancellationToken).ConfigureAwait(false) is Unavailable unavailable)
                {
                    _purgeErrors.Add(1, new KeyValuePair<string, object?>("reason", "retained_cleanup"));
                    logger.ZLogWarning($"Purging retained database {record.Database.Id:@Database} was unavailable: {unavailable.Message:@Reason}");
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                _purgeErrors.Add(1, new KeyValuePair<string, object?>("reason", "retained_cleanup"));
                logger.ZLogWarning(error, $"Purging retained database {record.Database.Id:@Database} failed");
            }
        }

        _expiryPassWaiting += waiting;
        _deletedAfter = page.Next;
        if (page.Next is null)
        {
            Interlocked.Exchange(ref _expiryWaiting, _expiryPassWaiting);
            _expiryPassWaiting = 0;
        }
    }
    private async ValueTask<RecordedDeletion> PurgeDeletedRecordAsync(DeletedDatabaseRecord record, CancellationToken cancellationToken)
    {
        using var authority = await retention.EnterAsync(record.Database.Id, cancellationToken).ConfigureAwait(false);
        if (authority is null)
        {
            return false;
        }
        if (await RetainedFilesUnavailableAsync(record, cancellationToken).ConfigureAwait(false) is { } missing)
        {
            return missing;
        }
        var result = await DeleteRecordedAsync(record.Storage, async (transaction, token) =>
        {
            authority.Fence(transaction);
            var current = await GetAsync(transaction, DeletedDatabases.Kind, record.Database.Id, ManagementJsonContext.Default.DeletedDatabaseRecord, token).ConfigureAwait(false);
            if (current is null || current.DeletedAt != record.DeletedAt || current.PurgeAt.AddHours(1) > time.GetUtcNow()
                || !DatabasePurge.SameDatabase(current.Storage, record.Storage))
            {
                return false;
            }

            if (current.Revision != record.Revision || !engine.HasDatabaseFiles(current.Storage, token))
            {
                return false;
            }
            _ = await UnregisterRecordAsync(transaction, record.Storage, record.Database, token, customerDeletion: true).ConfigureAwait(false);
            await DeletedDatabases.RemoveAsync(transaction, record, token).ConfigureAwait(false);
            return true;
        }, cancellationToken, authority).ConfigureAwait(false);
        if (result is true)
        {
            _ = await shredding.Value.DestroyAsync(record.Storage, DateTimeOffset.MaxValue, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private async ValueTask<Unavailable?> RetainedFilesUnavailableAsync(DeletedDatabaseRecord record, CancellationToken cancellationToken)
    {
        if (engine.HasDatabaseFiles(record.Storage, cancellationToken)
            && DatabaseRevision.TryParse(record.Revision, out var certified)
            && await ((IDatabaseRevisions)engine).CurrentRevisionAsync(record.Storage, cancellationToken).ConfigureAwait(false) is { } actual
            && actual.Incarnation == certified.Incarnation && DatabaseRevision.Compare(actual, certified) >= 0)
        {
            return null;
        }
        _purgeErrors.Add(1, new KeyValuePair<string, object?>("reason", "retained_files_missing"));
        logger.ZLogWarning($"Retained database {record.Database.Id:@Database} has no local files; purge preserves its keys and backups");
        return new Unavailable("copy not available on this node yet", "retained_copy_unavailable");
    }

}
