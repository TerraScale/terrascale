using TerraBase.Engine;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    // Called only under a version-2 permanent closure fence. Every holder advances bounded work independently;
    private async ValueTask<bool> EraseProjectBatchAsync(IStorageTransaction transaction, ProjectClosureRecord closure, CancellationToken token)
    {
        var project = closure.ProjectId;
        if (!await ClosureBindingMatchesAsync(transaction, project, closure.AuthorityOrganizationId, token).ConfigureAwait(false))
        {
            throw new InvalidOperationException("a project erasure conflicts with its storage binding");
        }

        var databases = await transaction.ListAsync(Databases(project), null, MemberPage, MaxPageBytes, token).ConfigureAwait(false);
        foreach (var document in databases.Documents)
        {
            var database = EntityPayload.Read(document, TerraBaseJsonContext.Default.ManagedDatabase)
                ?? throw new InvalidOperationException("a closing project's database is invalid");
            var namespaceId = await GetAsync(transaction, ProjectOrganizations, project, Id, token).ConfigureAwait(false);
            var organization = namespaceId is null ? null : await GetAsync(transaction, Organizations, namespaceId, TerraBaseJsonContext.Default.Organization, token).ConfigureAwait(false);
            if (organization is null || database.ProjectId != project || database.Id != document.Id)
            {
                throw new InvalidOperationException("a closing database has no matching storage namespace");
            }

            var storage = new TenantContext(organization.Id, organization.Plan, project, database.Id);
            _ = await UnregisterRecordAsync(transaction, storage, database, token, customerDeletion: true).ConfigureAwait(false);
        }

        await EngineApiKeyStore.RevokeProjectBatchAsync(transaction, project, MemberPage, MaxPageBytes, token).ConfigureAwait(false);
        if (databases.HasMore)
        {
            return false;
        }

        // All database purges captured the destination before its credentials leave authorization state.
        if (await GetAsync(transaction, ProjectBackupDestinations.Collection(project), "current", TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, token).ConfigureAwait(false) is { } destination)
        {
            await BackupDestinationHistory.SaveAsync(transaction, project, destination, token).ConfigureAwait(false);
            await transaction.DeleteAsync(ProjectBackupDestinations.Collection(project), "current", token).ConfigureAwait(false);
        }

        return await VerifyProjectPurgesAsync(transaction, project, token).ConfigureAwait(false)
            && await IsProjectEmptyAsync(transaction, project, token).ConfigureAwait(false);
    }

    // Both contracts wait for prior deletions: even a delayed v1 acceptance must certify active erasure.
    private async ValueTask<bool> VerifyProjectPurgesAsync(IStorageTransaction transaction, string project, CancellationToken token)
    {
        // Include deletion authorities predating this handoff: their catalog rows may already be gone.
        // Each holder scans its own history; another holder's progress cannot certify this one's coverage.
        var after = await GetAsync(transaction, ProjectClosure.Scans(project), options.MachineId, Id, token).ConfigureAwait(false);
        var scanned = after == "complete";
        if (!scanned)
        {
            var history = await transaction.ListAsync(DatabasePurge.Collection, after, MemberPage, MaxPageBytes, token).ConfigureAwait(false);
            foreach (var document in history.Documents)
            {
                var purge = EntityPayload.Read(document, TerraBaseJsonContext.Default.DatabasePurgeRecord)
                    ?? throw new InvalidOperationException("a database purge record is invalid");
                if (purge.Database.ProjectId == project)
                {
                    await PutAsync(transaction, ProjectClosure.Purges(project), document.Id, purge.Database, TerraBaseJsonContext.Default.TenantContext, token).ConfigureAwait(false);
                }
            }

            scanned = !history.HasMore;
            await PutAsync(transaction, ProjectClosure.Scans(project), options.MachineId, scanned ? "complete" : history.Documents[^1].Id, Id, token).ConfigureAwait(false);
        }

        var purges = await transaction.ListAsync(ProjectClosure.Purges(project), null, MemberPage, MaxPageBytes, token).ConfigureAwait(false);
        var absent = !purges.HasMore;
        foreach (var document in purges.Documents)
        {
            var purge = await GetAsync(transaction, DatabasePurge.Collection, document.Id, TerraBaseJsonContext.Default.DatabasePurgeRecord, token).ConfigureAwait(false);
            var storage = EntityPayload.Read(document, TerraBaseJsonContext.Default.TenantContext);
            if (purge is null || storage is null || storage.ProjectId != project || !DatabasePurge.SameDatabase(purge.Database, storage)
                || !await DatabasePurge.AreActiveCopiesAbsentAsync(transaction, purge, token).ConfigureAwait(false))
            {
                absent = false;
                continue;
            }

            await transaction.DeleteAsync(ProjectClosure.Purges(project), document.Id, token).ConfigureAwait(false);
        }

        return scanned && absent;
    }
}
