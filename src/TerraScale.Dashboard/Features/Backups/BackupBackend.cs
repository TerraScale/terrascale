using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TerraScale.Dashboard.Platform.Clients;
using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Features.Backups
{
    public sealed class BackupBackend(TerraBaseHttpClient terraBase, IProjectAccessTokens tokens, IDatabaseBackend databases) : IBackupBackend
    {
        public const int ProducerPageSize = 100;

        public async Task<IReadOnlyList<ProducerBackup>> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var result = new List<ProducerBackup>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? after = null;
            while (true)
            {
                var page = await RawPageAsync(scope, home, databaseId, after, ProducerPageSize, cancellationToken);
                foreach (var backup in page.Items)
                {
                    if (seen.Add(backup.Id))
                    {
                        result.Add(backup);
                    }
                }

                if (page.NextCursor is null)
                {
                    return result;
                }

                if (page.NextCursor == after)
                {
                    throw new HttpRequestException("Backup pagination did not advance.");
                }

                after = page.NextCursor;
            }
        }

        public async Task<BackupBackendPage> ListPageAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? after, int limit, CancellationToken cancellationToken)
        {
            var page = await RawPageAsync(scope, home, databaseId, after, limit, cancellationToken);
            return new(page.Items, string.IsNullOrEmpty(page.NextCursor) || page.NextCursor == after ? null : page.NextCursor);
        }

        private async Task<BackupBackendPage> RawPageAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? after, int limit, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            try
            {
                _ = await CallAsync(scope, home, ["databases:read"], caller => terraBase.GetBackupStatusAsync(caller, scope.ProjectId, database, cancellationToken), BackupBackendJson.Default.ContinuousBackupStatus, cancellationToken);
            }
            catch (DatabaseProviderException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)
            {
                return new([], null);
            }

            return new([], null);
        }

        public async Task<ProducerBackup> CreateAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            return await CallAsync(scope, home, ["databases:write"], caller => terraBase.CreateBackupAsync(caller, scope.ProjectId, database, cancellationToken), BackupBackendJson.Default.ProducerBackup, cancellationToken);
        }

        public async Task<ProducerBackupPolicy> PolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            return await CallAsync(scope, home, ["databases:read"], caller => terraBase.GetBackupPolicyAsync(caller, scope.ProjectId, database, cancellationToken), BackupBackendJson.Default.ProducerBackupPolicy, cancellationToken);
        }

        public async Task<ProducerBackupPolicy> SetPolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? fullBackupCron,
                int retentionDays, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            var request = JsonSerializer.SerializeToElement(new BackupPolicyRequest(fullBackupCron, retentionDays), BackupBackendJson.Default.BackupPolicyRequest);
            return await CallAsync(scope, home, ["databases:write"], caller => terraBase.SetBackupPolicyAsync(caller, scope.ProjectId, database, request, cancellationToken), BackupBackendJson.Default.ProducerBackupPolicy, cancellationToken, "backup-policy");
        }

        public async Task<BackupDestinationView?> DestinationAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken)
        {
            try
            {
                return await CallAsync(scope, home, ["databases:read"], caller => terraBase.GetBackupDestinationAsync(caller, scope.ProjectId, cancellationToken),
                    BackupBackendJson.Default.BackupDestinationView, cancellationToken);
            }
            catch (DatabaseProviderException error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public Task<BackupDestinationView> SetDestinationAsync(DatabaseScope scope, DatabaseRegion home, string endpoint, string region, string bucket,
            string accessKeyId, string secretKey, CancellationToken cancellationToken)
        {
            var request = JsonSerializer.SerializeToElement(new BackupDestinationRequest(endpoint, region, bucket, accessKeyId, secretKey, "both"), BackupBackendJson.Default.BackupDestinationRequest);
            return CallAsync(scope, home, ["databases:write"], caller => terraBase.SetBackupDestinationAsync(caller, scope.ProjectId, request, cancellationToken),
                BackupBackendJson.Default.BackupDestinationView, cancellationToken);
        }

        public async Task RemoveDestinationAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(home);
            var caller = ServiceCaller.User(await AccessAsync(scope, ["databases:write"], cancellationToken));
            var removed = await terraBase.DeleteBackupDestinationAsync(caller, scope.ProjectId, cancellationToken);
            var refused = removed switch
            {
                TerraBaseCompleted => null,
                TerraBaseRefusal refusal => refusal,
            };
            if (refused is not null)
            {
                throw Failure(refused.Exception(), "");
            }
        }

        public async Task<ProducerRestoreOperation> StartRestoreAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string backupId, string? name, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            var request = JsonSerializer.SerializeToElement(new BackupRestoreRequest(name), BackupBackendJson.Default.BackupRestoreRequest);
            var operation = await CallAsync(scope, home, ["databases:read", "databases:write"], caller => terraBase.RestoreBackupAsync(caller, scope.ProjectId, database, backupId, request, cancellationToken),
                BackupBackendJson.Default.ProducerRestoreOperation, cancellationToken, "restore");
            if (!TerraScaleIds.IsValid(operation.OperationId) || operation.Status is not ("running" or "completed" or "failed") ||
                operation.Status != "running" && operation.Database is null)
            {
                throw new HttpRequestException("Backup restoration returned an invalid operation.");
            }

            return operation;
        }

        public async Task<ProducerRestoreOperation> StartRestorePointInTimeAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, DateTimeOffset pointInTime, string? name, long? ttlSeconds, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            var operation = await CallAsync(scope, home, ["databases:read", "databases:write"], caller => terraBase.RestorePointInTimeAsync(caller, scope.ProjectId, database, new(pointInTime, name, null, ttlSeconds), cancellationToken),
                BackupBackendJson.Default.ProducerRestoreOperation, cancellationToken, "restore");
            if (!TerraScaleIds.IsValid(operation.OperationId) || operation.Status is not ("running" or "completed" or "failed") ||
                operation.Status != "running" && operation.Database is null)
            {
                throw new HttpRequestException("Point-in-time restore returned an invalid operation.");
            }

            return operation;
        }

        public async Task<ProducerRestoreOperation> RestoreOperationAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string operationId, CancellationToken cancellationToken)
        {
            var database = await NameAsync(scope, home, databaseId, cancellationToken);
            var operation = await CallAsync(scope, home, ["databases:read"], caller => terraBase.GetRestoreOperationAsync(caller, scope.ProjectId, database, operationId, cancellationToken),
                BackupBackendJson.Default.ProducerRestoreOperation, cancellationToken);
            if (operation.OperationId != operationId || operation.Status is not ("running" or "completed" or "failed") ||
                operation.Status != "running" && operation.Database is null)
            {
                throw new HttpRequestException("Backup restoration returned an invalid operation.");
            }

            return operation;
        }

        public async Task<ManagedDatabase> RestoreTargetAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string targetName, CancellationToken cancellationToken)
        {
            var token = await AccessAsync(scope, ["databases:read"], cancellationToken);
            var readback = (await terraBase.GetDatabaseAsync(ServiceCaller.User(token), scope.ProjectId, targetName, cancellationToken)).Require();
            var placed = new ManagedDatabase(readback.Id, readback.ProjectId, readback.Name, readback.CreatedAt, readback.BranchedFrom,
                readback.HomeRegion, readback.ReplicaRegions, readback.AllRegions);
            if (!TerraScaleIds.IsValid(placed.Id) || placed.ProjectId != scope.ProjectId ||
                placed.Id == databaseId || placed.BranchedFrom != databaseId || placed.Name != targetName ||
                placed.HomeRegion != home.Id || placed.ReplicaRegions is null)
            {
                throw new HttpRequestException("Backup restoration could not be confirmed in its project.");
            }

            return placed;
        }

        internal static string RestoreMessage(ProducerRestoreOperation operation)
        {
            return operation.ErrorCode switch
            {
                "database_limit" => "Your organization has reached its database limit. Remove an unused database or upgrade your plan, then restore again.",
                "name_taken" => "That database name is already in use. Choose a different name for the restored database.",
                _ => operation.ErrorMessage is { Length: > 0 } message ? message : "The restore could not be completed."
            };
        }

        private async Task<T> CallAsync<T>(DatabaseScope scope, DatabaseRegion home, string[] permissions, Func<ServiceCaller, Task<TerraBaseJsonOutcome>> call,
            JsonTypeInfo<T> type, CancellationToken cancellationToken, string operation = "")
        {
            ArgumentNullException.ThrowIfNull(home);
            var caller = ServiceCaller.User(await AccessAsync(scope, permissions, cancellationToken));
            var outcome = await call(caller);
            return outcome switch
            {
                JsonElement answer => answer.Deserialize(type) ?? throw new JsonException("Invalid backup response."),
                TerraBaseRefusal refusal => throw Failure(refusal.Exception(), operation),
            };
        }

        private async Task<string> AccessAsync(DatabaseScope scope, string[] permissions, CancellationToken ct)
        {
            if (scope.IsApiKey) return scope.AccessToken;
            var session = scope.Session ?? throw new HttpRequestException("Current user authorization is required.");
            return await tokens.GetAsync(session, scope.OrganizationId, scope.ProjectId, permissions, ct);
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
                : new AccountAccessException(message, error.Status, code);
        }

        private async Task<string> NameAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            if (scope.IsApiKey) return databaseId;
            return (await databases.ListAsync(scope, home, cancellationToken)).SingleOrDefault(item => item.Id == databaseId)?.Name
                ?? throw new DatabaseProviderException(HttpStatusCode.NotFound);
        }
    }

    internal sealed record BackupResponse(IReadOnlyList<ProducerBackup> Items, string? Next);
    internal sealed record ContinuousBackupStatus(string DatabaseId, string ArchivedThrough, DateTimeOffset EarliestRestorePointUtc, DateTimeOffset LatestRestorePointUtc, DateTimeOffset LastSnapshotAtUtc, long ArchivedBytes, int PendingSegments);
    internal sealed record BackupPolicyRequest(string? FullBackupCron, int RetentionDays);
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
    [JsonSerializable(typeof(ContinuousBackupStatus))]
    [JsonSerializable(typeof(ProducerBackup))]
    [JsonSerializable(typeof(ProducerBackupPolicy))]
    [JsonSerializable(typeof(BackupDestinationView))]
    [JsonSerializable(typeof(ManagedDatabase))]
    [JsonSerializable(typeof(BackupPolicyRequest))]
    [JsonSerializable(typeof(BackupRestoreRequest))]
    [JsonSerializable(typeof(ProducerRestoreOperation))]
    [JsonSerializable(typeof(BackupDestinationRequest))]
    internal sealed partial class BackupBackendJson : JsonSerializerContext;
}
