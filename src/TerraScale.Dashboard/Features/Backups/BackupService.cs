using Cronos;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Backups
{
    public sealed class BackupService(AccountService accounts, DatabaseService databases, IBackupBackend backend)
    {
        public async Task<BackupListing> ListAsync(AccountContext context, string databaseId, CancellationToken ct)
        {
            context = await databases.CurrentAsync(context, ct);
            var database = DatabaseService.Database(context, databaseId);
            var scope = DatabaseService.Scope(context);
            var home = await databases.HomeAsync(context, database, ct);
            var backupsRead = backend.ListAsync(scope, home, databaseId, ct);
            var policyRead = backend.PolicyAsync(scope, home, databaseId, ct);
            var backups = await backupsRead;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var backup in backups)
            {
                Validate(backup, databaseId);
                if (!ids.Add(backup.Id))
                {
                    throw new HttpRequestException("The provider returned duplicate backup identities.");
                }
            }
            BackupPolicyView? policy = null;
            var policyUnavailable = "";
            try { policy = Policy(await policyRead); }
            catch (Exception error) when (BackupEndpoints.KnownFailure(error) &&
                (error is not OperationCanceledException || !ct.IsCancellationRequested))
            { policyUnavailable = "Provider backup scheduling is unavailable."; }
            // Snapshot inventory and scheduler policy are independent provider reads.
            // A policy outage must not erase known restore points or their count.
            return new(policy, [.. backups.OrderByDescending(backup => backup.CreatedAt).ThenBy(backup => backup.Id, StringComparer.Ordinal).Select(View)])
            { PolicyUnavailable = policyUnavailable };
        }

        public async Task<BackupView> CreateAsync(AccountContext context, string databaseId, CancellationToken ct)
        {
            context = await databases.CurrentAsync(context, ct);
            var database = Manage(context, databaseId);
            var backup = await backend.CreateAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, ct), databaseId, ct);
            Validate(backup, databaseId);
            if (backup.Trigger != "manual")
            {
                throw new HttpRequestException("The provider did not confirm a manual backup.");
            }

            return View(backup);
        }

        public async Task<BackupPolicyView> SetPolicyAsync(AccountContext context, string databaseId, string fullBackupCron,
            string incrementalBackupCron, int retentionDays, bool disabled, CancellationToken ct)
        {
            context = await databases.CurrentAsync(context, ct);
            var database = Manage(context, databaseId);
            if (!string.IsNullOrWhiteSpace(incrementalBackupCron))
            {
                throw new AccountAccessException("Incremental schedules are not offered. Leave the incremental schedule empty.", 422);
            }

            if (fullBackupCron.Length > 256)
            {
                throw new AccountAccessException("Enter a valid full-backup cron schedule or leave it empty.", 422);
            }

            fullBackupCron = string.Join(' ', fullBackupCron.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (!ValidCron(fullBackupCron))
            {
                throw new AccountAccessException("Enter a valid full-backup cron schedule or leave it empty.", 422);
            }

            if (retentionDays is < 1 or > 35)
            {
                throw new AccountAccessException("Set snapshot retention from 1 to 35 days.", 422);
            }

            var policy = await backend.SetPolicyAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, ct), databaseId,
                fullBackupCron.Length == 0 ? null : fullBackupCron, retentionDays, disabled, ct);
            var saved = Policy(policy);
            if (saved.FullBackupCron != fullBackupCron || saved.RetentionDays != retentionDays || saved.Disabled != disabled || saved.UpdatedAt is null)
            {
                throw new HttpRequestException("The provider did not confirm the submitted backup policy.");
            }

            return saved;
        }

        public async Task<BackupRestoreResult> RestoreAsync(AccountContext context, string databaseId, string backupId, string name, CancellationToken ct)
        {
            context = await databases.CurrentAsync(context, ct);
            var source = Manage(context, databaseId);
            DatabaseService.DemandProvision(context.Organization!, context.UserId);
            DatabaseService.DemandRoom(context.Organization!);
            name = string.IsNullOrWhiteSpace(name) ? "" : DatabaseService.ValidName(name);
            if (name.Length > 0)
            {
                DatabaseService.DemandName(context.Project!, name);
            }

            var listing = await ListAsync(context, databaseId, ct);
            backupId = backupId.Length == 0 ? listing.Backups.FirstOrDefault()?.Id ?? "" : backupId;
            if (!TerraScaleIds.IsValid(backupId) || !listing.Backups.Any(backup => backup.Id == backupId))
            {
                throw new AccountAccessException("That restore point is no longer available.", 404);
            }
            // The producer returns only after a new snapshot copy has completed.
            // No local documents or cached metadata can claim a successful restore.
            var scope = DatabaseService.Scope(context);
            var home = await databases.HomeAsync(context, source, ct);
            var restored = await backend.RestoreAsync(scope, home, databaseId, backupId, name.Length == 0 ? null : name, ct);
            return await RecordRestoreAsync(context, source, home, restored, name, ct);
        }

        internal async Task<BackupRestoreResult> RecordRestoreAsync(AccountContext context, DatabaseRecord source, DatabaseRegion home, ManagedDatabase restored, string name, CancellationToken ct)
        {
            var scope = DatabaseService.Scope(context);
            string returnedName;
            try { returnedName = DatabaseService.ValidName(restored.Name); }
            catch (AccountAccessException error) { throw new HttpRequestException("The provider returned an invalid restored database name.", error); }
            if (name.Length > 0 && returnedName != name || !TerraScaleIds.IsValid(restored.Id) || restored.ProjectId != scope.ProjectId ||
                restored.Id == source.Id || restored.BranchedFrom != source.Id || restored.CreatedAt == default)
            {
                throw new HttpRequestException("The provider returned an invalid restored database.");
            }

            if (restored.HomeRegion != home.Id)
            {
                throw new HttpRequestException("The restored database has a different home region.");
            }

            var placement = await databases.RemotePlacementAsync(restored, ct);
            var record = DatabaseService.Record(restored, placement, "ready");
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                DatabaseService.DemandProvision(org, context.UserId);
                var project = org.Projects.GetValueOrDefault(scope.ProjectId) ?? throw new AccountAccessException("Project is unavailable.", 404);
                if (!project.Databases.ContainsKey(source.Id))
                {
                    throw new AccountAccessException("Source database is unavailable.", 404);
                }

                DatabaseService.DemandName(project, record.Name);
                if (project.Databases.ContainsKey(record.Id))
                {
                    throw new AccountAccessException("The restored database identity is already in use.", 409);
                }

                DatabaseService.DemandRoom(org);
                project.Databases.Add(record.Id, record);
                org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "database.restored", record.Name, "database", accounts.Now));
            }, ct);
            return new(record.Id, record.Name, record.Status);
        }

        public async Task<BackupExportView> ExportAsync(AccountContext context, string databaseId, CancellationToken ct)
        {
            context = await databases.CurrentAsync(context, ct);
            var scope = DatabaseService.Scope(context);
            var home = await databases.HomeAsync(context, DatabaseService.Database(context, databaseId), ct);
            var destination = backend.DestinationAsync(scope, home, ct);
            var schedule = await backend.ExportScheduleAsync(scope, home, databaseId, ct);
            if (schedule.LastRun is { Status: not ("succeeded" or "failed") } || schedule.Cron is { Length: > 0 } cron && !ValidCron(cron))
            {
                throw new HttpRequestException("The provider returned an invalid export schedule.");
            }

            return new(await destination, schedule);
        }

        // Credentials go to TerraBase, which seals them; the dashboard never stores or shows them again.
        public async Task<BackupDestinationView> SetDestinationAsync(AccountContext context, string databaseId, string endpoint, string region, string bucket,
            string accessKeyId, string secretKey, CancellationToken ct)
        {
            context = await databases.CurrentAsync(context, ct);
            var home = await databases.HomeAsync(context, Manage(context, databaseId), ct);
            (endpoint, region, bucket, accessKeyId) = (endpoint.Trim(), region.Trim(), bucket.Trim(), accessKeyId.Trim());
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || region.Length is 0 or > 64 ||
                bucket.Length is < 3 or > 63 || accessKeyId.Length is 0 or > 256 || secretKey.Length is 0 or > 256)
            {
                throw new AccountAccessException("Enter an https endpoint, the region, a bucket name of 3 to 63 characters, and the access key ID and secret.", 422);
            }

            BackupDestinationView saved;
            try { saved = await backend.SetDestinationAsync(DatabaseService.Scope(context), home, endpoint, region, bucket, accessKeyId, secretKey, ct); }
            catch (DatabaseProviderException error) when (error.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            { throw new AccountAccessException("TerraBase could not access that bucket. Check the endpoint, region, bucket and key, then try again.", 503); }
            if (saved.Bucket != bucket)
            {
                throw new HttpRequestException("The provider did not confirm the submitted bucket.");
            }

            await accounts.RecordAsync(context, org => org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "backup.destination_updated", bucket, "backup_destination", accounts.Now)), ct);
            return saved;
        }

        public async Task RemoveDestinationAsync(AccountContext context, string databaseId, CancellationToken ct)
        {
            context = await databases.CurrentAsync(context, ct);
            await backend.RemoveDestinationAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, Manage(context, databaseId), ct), ct);
            await accounts.RecordAsync(context, org => org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "backup.destination_removed", context.Project!.Name, "backup_destination", accounts.Now)), ct);
        }

        public async Task<ExportScheduleView> SetExportScheduleAsync(AccountContext context, string databaseId, string cron, CancellationToken ct)
        {
            context = await databases.CurrentAsync(context, ct);
            var database = Manage(context, databaseId);
            cron = string.Join(' ', cron.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (!ValidCron(cron))
            {
                throw new AccountAccessException("Choose off, daily or weekly, or enter a valid 5-field UTC cron schedule.", 422);
            }

            try { return await backend.SetExportScheduleAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, ct), databaseId, cron.Length == 0 ? null : cron, ct); }
            catch (DatabaseProviderException error) when (error.StatusCode == System.Net.HttpStatusCode.Conflict)
            { throw new AccountAccessException("Save your bucket for this project before scheduling exports.", 409); }
        }

        public static void RejectOverwrite()
        {
            throw new AccountAccessException("The backup provider restores into a new database and cannot overwrite this database.", 503);
        }

        private static DatabaseRecord Manage(AccountContext context, string id)
        {
            var database = DatabaseService.Database(context, id);
            if (!context.CanManage)
            {
                throw new AccountAccessException("Organization management access is required.");
            }

            if (database.Status != "ready")
            {
                throw new AccountAccessException("Only a ready database can be backed up or restored.", 409);
            }

            return database;
        }
        private static BackupView View(ProducerBackup backup)
        {
            return new(backup.Id, backup.Status, backup.CreatedAt, backup.SizeBytes);
        }

        private static void Validate(ProducerBackup backup, string databaseId)
        {
            if (!TerraScaleIds.IsValid(backup.Id) || backup.DatabaseId != databaseId || backup.Status != "ready" ||
                backup.Trigger is not ("manual" or "scheduled") || backup.SizeBytes < 0 || backup.CreatedAt == default)
            {
                throw new HttpRequestException("The provider returned an invalid completed backup.");
            }
        }
        private static BackupPolicyView Policy(ProducerBackupPolicy policy)
        {
            var cron = string.IsNullOrWhiteSpace(policy.FullBackupCron) ? "" : policy.FullBackupCron.Trim();
            if (policy.RetentionDays is < 1 or > 35 || !ValidCron(cron))
            {
                throw new HttpRequestException("The provider returned an invalid backup policy.");
            }

            return new(cron, "", policy.RetentionDays, policy.Disabled, policy.UpdatedAt);
        }
        private static bool ValidCron(string cron)
        {
            return cron.Length <= 256 &&
            (cron.Length == 0 || CronExpression.TryParse(cron, CronFormat.Standard, out _));
        }
    }
}
