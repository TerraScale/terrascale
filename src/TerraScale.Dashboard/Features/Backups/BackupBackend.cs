using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Backups;
using TerraScale.TerraBase.Contracts.Management;
using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Features.Backups
{
    // Backup calls retain current-user scopes and route to the database home region.
    public sealed class BackupBackend(TerraBaseRpcClient rpc, IProjectAccessTokens tokens, IDatabaseBackend databases) : IBackupBackend
    {
        public async Task<IReadOnlyList<ProducerBackup>> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            var result = new List<ProducerBackup>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? after = null;
            do
            {
                var page = await CallAsync(scope, home, "databases:read", client => client.ListAsync(scope.ProjectId, database, after).ResponseAsync, BackupBackendJson.Default.BackupResponse, cancellationToken);
                result.AddRange(page.Items);
                after = page.Next;
                if (!string.IsNullOrEmpty(after) && !cursors.Add(after))
                {
                    throw new HttpRequestException("Backup pagination did not advance.");
                }
            } while (!string.IsNullOrEmpty(after));
            return result;
        }

        public async Task<ProducerBackup> CreateAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            return await CallAsync(scope, home, "databases:write", client => client.CreateAsync(scope.ProjectId, database).ResponseAsync, BackupBackendJson.Default.ProducerBackup, cancellationToken);
        }

        public async Task<ProducerBackupPolicy> PolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            return await CallAsync(scope, home, "databases:read", client => client.GetPolicyAsync(scope.ProjectId, database).ResponseAsync, BackupBackendJson.Default.ProducerBackupPolicy, cancellationToken);
        }

        public async Task<ProducerBackupPolicy> SetPolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? fullBackupCron,
                int retentionDays, bool disabled, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            var request = JsonSerializer.SerializeToElement(new BackupPolicyRequest(fullBackupCron, retentionDays, disabled), BackupBackendJson.Default.BackupPolicyRequest);
            return await CallAsync(scope, home, "databases:write", client => client.SetPolicyAsync(scope.ProjectId, database, request).ResponseAsync, BackupBackendJson.Default.ProducerBackupPolicy, cancellationToken, "backup-policy");
        }

        // The destination is the project's; mode both keeps platform backups and mirrors them to the customer's bucket.
        public async Task<BackupDestinationView?> DestinationAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken)
        {
            try { return await CallAsync(scope, home, "databases:read", client => client.GetDestinationAsync(scope.ProjectId).ResponseAsync, BackupBackendJson.Default.BackupDestinationView, cancellationToken); }
            catch (DatabaseProviderException error) when (error.StatusCode == HttpStatusCode.NotFound) { return null; }
        }
        public Task<BackupDestinationView> SetDestinationAsync(DatabaseScope scope, DatabaseRegion home, string endpoint, string region, string bucket,
            string accessKeyId, string secretKey, CancellationToken cancellationToken)
        {
            var request = JsonSerializer.SerializeToElement(new BackupDestinationRequest(endpoint, region, bucket, accessKeyId, secretKey, "both"), BackupBackendJson.Default.BackupDestinationRequest);
            return CallAsync(scope, home, "databases:write", client => client.SetDestinationAsync(scope.ProjectId, request).ResponseAsync, BackupBackendJson.Default.BackupDestinationView, cancellationToken);
        }

        public async Task RemoveDestinationAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken)
        {
            var client = await ClientAsync(scope, home, "databases:write", cancellationToken);
            try { await client.RemoveDestinationAsync(scope.ProjectId); }
            catch (TerraBaseException error) { throw Failure(error, ""); }
        }

        public async Task<ExportScheduleView> ExportScheduleAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            return await CallAsync(scope, home, "databases:read", client => client.GetExportScheduleAsync(scope.ProjectId, database).ResponseAsync, BackupBackendJson.Default.ExportScheduleView, cancellationToken);
        }

        public async Task<ExportScheduleView> SetExportScheduleAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? cron, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            var request = JsonSerializer.SerializeToElement(new ExportScheduleRequest(cron), BackupBackendJson.Default.ExportScheduleRequest);
            return await CallAsync(scope, home, "databases:write", client => client.SetExportScheduleAsync(scope.ProjectId, database, request).ResponseAsync, BackupBackendJson.Default.ExportScheduleView, cancellationToken);
        }

        public async Task<ManagedDatabase> RestoreAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string backupId, string? name, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            var request = JsonSerializer.SerializeToElement(new BackupRestoreRequest(name), BackupBackendJson.Default.BackupRestoreRequest);
            var restored = await CallAsync(scope, home, "databases:write", client => client.RestoreBackupAsync(scope.ProjectId, database, backupId, request).ResponseAsync,
                BackupBackendJson.Default.ManagedDatabase, cancellationToken, "restore");
            if (!TerraScaleIds.IsValid(restored.Id) || restored.ProjectId != scope.ProjectId ||
                restored.Id == databaseId || restored.BranchedFrom != databaseId)
            {
                throw new HttpRequestException("Backup restoration returned an invalid database identity.");
            }
            // The restore route returns the raw catalog record: null replicas is legitimate there.
            // The managed GET body normalizes the actual current placement.
            var token = await AccessAsync(scope, "databases:read", cancellationToken);
            var readback = await rpc.User<IManagementService>(token, home.Id).WithCancellationToken(cancellationToken).GetDatabaseAsync(scope.ProjectId, restored.Name);
            var placed = new ManagedDatabase(readback.Id, readback.ProjectId, readback.Name, readback.CreatedAt, readback.BranchedFrom,
                readback.HomeRegion, readback.ReplicaRegions, readback.AllRegions);
            if (placed.Id != restored.Id || placed.ProjectId != scope.ProjectId || placed.Name != restored.Name ||
                placed.BranchedFrom != databaseId || placed.CreatedAt != restored.CreatedAt ||
                placed.HomeRegion != home.Id || placed.ReplicaRegions is null)
            {
                throw new HttpRequestException("Backup restoration could not be confirmed in its project.");
            }

            return placed;
        }

        private async Task<T> CallAsync<T>(DatabaseScope scope, DatabaseRegion home, string permission, Func<IBackupsService, Task<JsonElement>> call,
            JsonTypeInfo<T> type, CancellationToken cancellationToken, string operation = "")
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var client = await ClientAsync(scope, home, permission, timeout.Token);
            try { return (await call(client)).Deserialize(type) ?? throw new JsonException("Invalid backup response."); }
            catch (TerraBaseException error) { throw Failure(error, operation); }
        }

        private async Task<IBackupsService> ClientAsync(DatabaseScope scope, DatabaseRegion home, string permission, CancellationToken ct)
        {
            return rpc.User<IBackupsService>(await AccessAsync(scope, permission, ct), home.Id).WithCancellationToken(ct);
        }

        private async Task<string> AccessAsync(DatabaseScope scope, string permission, CancellationToken ct)
        {
            var session = scope.Session ?? throw new HttpRequestException("Current user authorization is required.");
            return await tokens.GetAsync(session, scope.OrganizationId, scope.ProjectId, [permission], ct);
        }

        private static Exception Failure(TerraBaseException error, string path)
        {
            if (error is TerraBaseWrongRegionException wrong)
            {
                return new DatabaseWrongRegionException(wrong.Region, wrong.HomeRegion, wrong.Regions);
            }

            var code = error.ErrorCode;
            var message = (error.Status, code) switch
            {
                (409, "database_limit") => "Your organization has reached its database limit. Remove an unused database or upgrade your plan, then restore again.",
                (409, "name_taken") => "That database name is already in use. Choose a different name for the restored database.",
                (404, "not_found") when path == "restore" => "That snapshot could not be opened. Refresh the backup list and try again. If it is still listed, contact support.",
                (429 or 503, "capacity") => "Backup capacity is temporarily unavailable. Wait a moment, then try again.",
                (400, "invalid_request") when path == "backup-policy" => "Set snapshot retention from 1 to 35 days and use a valid 5-field UTC cron schedule.",
                (400, "invalid_request") when path == "restore" => "Choose a valid name for the restored database. " + DatabaseService.NameHint + " Leave the name blank to choose it automatically.",
                _ => null
            };
            return message is null ? new DatabaseProviderException((HttpStatusCode)error.Status) { RetryAfter = error.RetryAfter }
                : new AccountAccessException(message, error.Status, code!);
        }

        // UI URLs use database IDs; the authoritative project list resolves them to the route names TerraBase expects.
        private async Task<string> NameAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            return (await databases.ListAsync(scope, home, cancellationToken)).SingleOrDefault(item => item.Id == databaseId)?.Name
                ?? throw new DatabaseProviderException(HttpStatusCode.NotFound);
        }
    }

    internal sealed record BackupResponse(IReadOnlyList<ProducerBackup> Items, string? Next);
    internal sealed record BackupPolicyRequest(string? FullBackupCron, int RetentionDays, bool Disabled);
    internal sealed record ExportScheduleRequest(string? Cron);
    internal sealed record BackupRestoreRequest(string? Name);
    internal sealed record BackupDestinationRequest(string Endpoint, string Region, string Bucket, string AccessKeyId, string SecretKey, string Mode)
    {
        public override string ToString()
        {
            return "Backup destination [REDACTED]";
        }
    }

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
    [JsonSerializable(typeof(BackupResponse))]
    [JsonSerializable(typeof(ProducerBackup))]
    [JsonSerializable(typeof(ProducerBackupPolicy))]
    [JsonSerializable(typeof(BackupDestinationView))]
    [JsonSerializable(typeof(ExportScheduleView))]
    [JsonSerializable(typeof(ManagedDatabase))]
    [JsonSerializable(typeof(BackupPolicyRequest))]
    [JsonSerializable(typeof(ExportScheduleRequest))]
    [JsonSerializable(typeof(BackupRestoreRequest))]
    [JsonSerializable(typeof(BackupDestinationRequest))]
    internal sealed partial class BackupBackendJson : JsonSerializerContext;
}
