using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Qos;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private string? _deletedAfter;

    private async ValueTask<DeletedDatabaseRecord> RetainDeletedAsync(
        IStorageTransaction transaction, TenantContext storage, ManagedDatabase database, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var record = new DeletedDatabaseRecord(storage, database, now, now.AddDays(7));
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
        return await WriteAsync<SetDatabaseRegionsResult>(async transaction =>
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

            if (await transaction.GetAsync(DatabasePurge.Kind, databaseId, cancellationToken).ConfigureAwait(false) is not null)
            {
                return new Conflict("permanent deletion has already begun", "database_purging");
            }

            if (record.PurgeAt <= time.GetUtcNow())
            {
                return new Conflict("the seven-day restore window has ended", "restore_window_expired");
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

            if (!engine.HasDatabaseFiles(record.Storage, cancellationToken))
            {
                return new Unavailable("the retained database files are unavailable");
            }

            var restored = record.Database with { ExpiresAt = null };
            await CatalogTables.PutDatabaseAsync(transaction, restored, cancellationToken).ConfigureAwait(false);
            await RemoveDeletedAsync(transaction, record, cancellationToken).ConfigureAwait(false);
            return restored;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DeleteDatabaseResult> PurgeDeletedDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken)
    {
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
        var result = await DeleteRecordedAsync(retained.Storage, async (transaction, token) =>
        {
            if (await AuthorizedProjectAsync(transaction, userId, organizationId, projectId, token).ConfigureAwait(false) is not TenantContext)
            {
                return false;
            }
            var current = await GetAsync(transaction, DeletedDatabases.Kind, databaseId, ManagementJsonContext.Default.DeletedDatabaseRecord, token).ConfigureAwait(false);
            if (current is null || current.DeletedAt != retained.DeletedAt || !DatabasePurge.SameDatabase(current.Storage, retained.Storage))
            {
                return false;
            }
            _ = await UnregisterRecordAsync(transaction, current.Storage, current.Database, token, customerDeletion: true).ConfigureAwait(false);
            await RemoveDeletedAsync(transaction, current, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        if (result is true)
        {
            _ = await shredding.Value.DestroyAsync(retained.Storage, DateTimeOffset.MaxValue, cancellationToken).ConfigureAwait(false);
        }
        return result switch
        {
            true => new Done(),
            false => new Conflict("the deleted database changed during purge", "database_purging"),
            Unavailable error => error,
        };
    }

    private static async ValueTask RemoveDeletedAsync(IStorageTransaction transaction, DeletedDatabaseRecord record, CancellationToken cancellationToken)
    {
        _ = await transaction.DeleteAsync(DeletedDatabases.Kind, record.Database.Id, cancellationToken).ConfigureAwait(false);
        _ = await transaction.DeleteAsync(DeletedDatabases.Project(record.Database.ProjectId), record.Database.Id, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PurgeDeletedDatabasesAsync(CancellationToken cancellationToken)
    {
        var page = await ReadAsync<ManagementPage<DeletedDatabaseRecord>?>(async transaction => await SystemCatalog.ListAsync(
            transaction, DeletedDatabases.Kind, _deletedAfter, CatalogPage, ManagementJsonContext.Default.DeletedDatabaseRecord,
            record => record.Database.Id, cancellationToken).ConfigureAwait(false), static _ => null, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return;
        }

        foreach (var record in page.Items.Where(record => record.PurgeAt <= time.GetUtcNow() && record.Database.Home(options.Region) == options.Region))
        {
            try
            {
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

        _deletedAfter = page.Next;
    }
    private async ValueTask<RecordedDeletion> PurgeDeletedRecordAsync(DeletedDatabaseRecord record, CancellationToken cancellationToken)
    {
        var result = await DeleteRecordedAsync(record.Storage, async (transaction, token) =>
        {
            var current = await GetAsync(transaction, DeletedDatabases.Kind, record.Database.Id, ManagementJsonContext.Default.DeletedDatabaseRecord, token).ConfigureAwait(false);
            if (current is null || current.DeletedAt != record.DeletedAt || current.PurgeAt > time.GetUtcNow()
                || !DatabasePurge.SameDatabase(current.Storage, record.Storage))
            {
                return false;
            }

            _ = await UnregisterRecordAsync(transaction, record.Storage, record.Database, token, customerDeletion: true).ConfigureAwait(false);
            await RemoveDeletedAsync(transaction, record, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        if (result is true)
        {
            _ = await shredding.Value.DestroyAsync(record.Storage, DateTimeOffset.MaxValue, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

}
