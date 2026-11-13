using System.Diagnostics;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    public ValueTask<PurgeDatabaseResult> GetDatabasePurgeAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ReadAsync<PurgeDatabaseResult>(async transaction =>
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
            var job = await GetAsync(transaction, DatabasePurge.Kind, databaseId, ManagementJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
            if (job is null || job.Database.OrganizationId != organizationId || job.Database.ProjectId != projectId)
            {
                return ManagementErrors.NotFound;
            }
            var progress = await GetAsync(transaction, DatabaseBackupPurge.ProgressKind, databaseId,
                BackupPurgeJsonContext.Default.BackupPurgeProgress, cancellationToken).ConfigureAwait(false);
            var keys = options.Backup is null || progress is { KeysShreddedAt: not null };
            var local = await DatabasePurge.AreActiveCopiesAbsentAsync(transaction, job, cancellationToken).ConfigureAwait(false);
            var backups = options.Backup is null || job.BackupVerifiedAt is not null;
            List<string> outstanding = [];
            if (!local || !keys)
            {
                outstanding.Add("files");
            }
            if (!keys)
            {
                outstanding.Add("keys");
            }
            if (!backups)
            {
                outstanding.Add("backups");
            }
            return new DatabasePurgeBody(databaseId, outstanding.Count == 0 ? "complete" : "destroying", outstanding);
        }, static error => error, cancellationToken);

    private async Task AdvancePurgeAsync(string databaseId, CancellationToken token)
    {
        var job = await ReadAsync(transaction => GetAsync(transaction, DatabasePurge.Kind, databaseId,
            ManagementJsonContext.Default.DatabasePurgeRecord, token), static _ => null, token).ConfigureAwait(false);
        if (job is null)
        {
            return;
        }
        await DropLocalAsync(job.Database, token).ConfigureAwait(false);
        _ = await shredding.Value.DestroyAsync(job.Database, time.GetUtcNow().AddSeconds(10), token).ConfigureAwait(false);
    }
}
