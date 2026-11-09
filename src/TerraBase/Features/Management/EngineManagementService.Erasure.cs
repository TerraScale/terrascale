using TerraBase.Engine;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private async ValueTask<bool> EraseProjectBatchAsync(IStorageTransaction transaction, ProjectClosureRecord closure, CancellationToken token)
    {
        var project = closure.ProjectId;
        if (!await ClosureOrganizationMatchesAsync(transaction, project, closure.AuthorityOrganizationId, token).ConfigureAwait(false))
        {
            return false;
        }

        var databases = await CatalogTables.DatabasesAsync(transaction, project, null, CatalogPage, token).ConfigureAwait(false);
        foreach (var database in databases.Items)
        {
            var namespaceId = await CatalogTables.ProjectOrganizationAsync(transaction, project, token).ConfigureAwait(false);
            var organization = (namespaceId is null ? null : await CatalogTables.OrganizationAsync(transaction, namespaceId, token).ConfigureAwait(false))
                ?? throw new InvalidOperationException("a closing database has no matching storage namespace");

            var storage = new TenantContext(organization.Id, organization.Plan, project, database.Id);
            _ = await UnregisterRecordAsync(transaction, storage, database, token, customerDeletion: true).ConfigureAwait(false);
        }

        await EngineApiKeyStore.RevokeProjectBatchAsync(transaction, project, CatalogPage, MaxPageBytes, token).ConfigureAwait(false);
        if (databases.Next is not null)
        {
            return false;
        }

        if (await GetAsync(transaction, ProjectBackupDestinations.Kind(project), "current", BackupJsonContext.Default.ProjectBackupDestinationRecord, token).ConfigureAwait(false) is { } destination)
        {
            await BackupDestinationHistory.SaveAsync(transaction, project, destination, token).ConfigureAwait(false);
            await transaction.DeleteAsync(ProjectBackupDestinations.Kind(project), "current", token).ConfigureAwait(false);
        }

        return await VerifyProjectPurgesAsync(transaction, project, token).ConfigureAwait(false)
            && await IsProjectEmptyAsync(transaction, project, token).ConfigureAwait(false);
    }

    private async ValueTask<bool> VerifyProjectPurgesAsync(IStorageTransaction transaction, string project, CancellationToken token)
    {
        var after = await GetAsync(transaction, ProjectClosure.Scans(project), options.MachineId, Id, token).ConfigureAwait(false);
        var scanned = after == "complete";
        if (!scanned)
        {
            var history = await transaction.ListAsync(DatabasePurge.Kind, after, CatalogPage, MaxPageBytes, token).ConfigureAwait(false);
            foreach (var stored in history.Records)
            {
                var purge = EntityPayload.Read(stored, ManagementJsonContext.Default.DatabasePurgeRecord)
                    ?? throw new InvalidOperationException("a database purge record is invalid");
                if (purge.Database.ProjectId == project)
                {
                    await PutAsync(transaction, ProjectClosure.Purges(project), stored.Id, purge.Database, TerraBaseJsonContext.Default.TenantContext, token).ConfigureAwait(false);
                }
            }

            scanned = !history.HasMore;
            await PutAsync(transaction, ProjectClosure.Scans(project), options.MachineId, scanned ? "complete" : history.Records[^1].Id, Id, token).ConfigureAwait(false);
        }

        var purges = await transaction.ListAsync(ProjectClosure.Purges(project), null, CatalogPage, MaxPageBytes, token).ConfigureAwait(false);
        var absent = !purges.HasMore;
        foreach (var stored in purges.Records)
        {
            var purge = await GetAsync(transaction, DatabasePurge.Kind, stored.Id, ManagementJsonContext.Default.DatabasePurgeRecord, token).ConfigureAwait(false);
            var storage = EntityPayload.Read(stored, TerraBaseJsonContext.Default.TenantContext);
            if (purge is null || storage is null || storage.ProjectId != project || !DatabasePurge.SameDatabase(purge.Database, storage)
                || !await DatabasePurge.AreActiveCopiesAbsentAsync(transaction, purge, token).ConfigureAwait(false)
                || options.Backup is not null && (await GetAsync(transaction, DatabaseBackupPurge.ProgressKind, stored.Id,
                    BackupPurgeJsonContext.Default.BackupPurgeProgress, token).ConfigureAwait(false))?.KeysShreddedAt is null)
            {
                absent = false;
                continue;
            }

            await transaction.DeleteAsync(ProjectClosure.Purges(project), stored.Id, token).ConfigureAwait(false);
        }

        return scanned && absent;
    }
}
