using System.Text;
using Amazon.S3;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Backups;

internal sealed class ProjectBackupDestinations(
    SystemCatalog catalog, TerraBaseOptions options, IHttpClientFactory http, TimeProvider time, IBackupSecretLifecycle lifecycle, IOciVaultSecrets? secrets = null) : IProjectBackupDestinations
{
    internal static string Collection(string projectId) => ProjectState(projectId) + "backup_destination";
    private const string Current = "current";
    private const int MaxSaves = 4;
    private readonly Lock _savingLock = new();
    // At most four entries, one per organization; rejected callers never form a queue.
    private readonly HashSet<string> _saving = [with(StringComparer.Ordinal)];

    public ValueTask<ProjectBackupDestinationRecord?> ReadAsync(string organizationId, string projectId, CancellationToken cancellationToken) =>
        catalog.ReadAsync(transaction => GetAsync(transaction, Collection(projectId), Current, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken), cancellationToken);

    public async Task<SaveBackupDestinationResult> SaveAsync(
        string userId, string organizationId, string projectId, ProjectBackupDestinationRequest request, CancellationToken cancellationToken)
    {
        if (await catalog.ReadAsync(transaction => WritableAsync(transaction, userId, organizationId, projectId, cancellationToken), cancellationToken).ConfigureAwait(false) is var access
            && access != ManagementError.None)
        {
            return access == ManagementError.Forbidden ? ManagementErrors.RoleTooLow : new NotFound();
        }

        if (options.Backup is null || secrets is null)
        {
            return new Unavailable("customer backup Vault settings are not configured");
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_savingLock)
        {
            if (_saving.Count == MaxSaves || !_saving.Add(organizationId))
            {
                return new Unavailable("customer backup configuration request limit reached");
            }
        }

        BackupSecretWork? intent = null;
        BackupSecretWork? retirement = null;
        string? secretId = null;
        var confirmedNotCreated = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using (var bucket = new S3ObjectStore(new Uri(request.Endpoint!), request.Region!, request.Bucket!, request.AccessKeyId!, request.SecretKey!, http,
                S3ObjectStore.CustomerHttpClientName, time))
            {
                await ProbeAsync(bucket, organizationId, projectId, timeout.Token).ConfigureAwait(false);
            }

            intent = await lifecycle.BeginCreateAsync(timeout.Token).ConfigureAwait(false);
            if (intent is null)
            {
                return new Unavailable("customer backup credential lifecycle backlog is full");
            }

            secretId = await secrets.CreateAsync(intent.SecretName!, request.AccessKeyId!, request.SecretKey!, timeout.Token).ConfigureAwait(false);
            var destination = new ProjectBackupDestination(new Uri(request.Endpoint!), request.Region!, request.Bucket!, request.Mode!, time.GetUtcNow());
            var record = new ProjectBackupDestinationRecord(1, intent.Id, destination, secretId);
            var (saved, retired) = await SaveCoreAsync(userId, organizationId, projectId, record, timeout.Token).ConfigureAwait(false);
            if (saved is ProjectBackupDestination)
            {
                // Publication removed this intent and persisted its predecessor's retirement atomically.
                intent = null;
                retirement = retired;
            }

            return saved;
        }
        catch (OciSecretNotCreatedException)
        {
            confirmedNotCreated = true;
            cancellationToken.ThrowIfCancellationRequested();
            return new Unavailable("customer backup bucket or OCI Vault could not be accessed");
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or AmazonS3Exception or IOException or InvalidDataException or OperationCanceledException)
        {
            // Bucket/Vault errors can include tenant input and credentials. Only a fixed error reaches HTTP.
            return new Unavailable("customer backup bucket or OCI Vault could not be accessed");
        }
        finally
        {
            try
            {
                if (intent is not null && confirmedNotCreated)
                {
                    // Hold bounded save admission until local removal ends; remote cleanup cannot reject it.
                    await lifecycle.AbandonCreateAsync(intent.Id, secretId, confirmedNotCreated: true).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_savingLock)
                {
                    _saving.Remove(organizationId);
                }
            }

            if (intent is not null && !confirmedNotCreated)
            {
                await lifecycle.AbandonCreateAsync(intent.Id, secretId, confirmedNotCreated: false).ConfigureAwait(false);
            }

            await lifecycle.RetireCommittedAsync(retirement).ConfigureAwait(false);
        }
    }

    private async Task<(SaveBackupDestinationResult Result, BackupSecretWork? Retirement)> SaveCoreAsync(
        string userId, string organizationId, string projectId, ProjectBackupDestinationRecord record, CancellationToken cancellationToken) =>
        await catalog.WriteDurableAsync<(SaveBackupDestinationResult, BackupSecretWork?)>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (access != ManagementError.None)
            {
                return (access == ManagementError.Forbidden ? ManagementErrors.RoleTooLow : new NotFound(), null);
            }

            var replaced = await GetAsync(transaction, Collection(projectId), Current, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false);
            if (!await lifecycle.CanPublishAsync(transaction, record.Revision, cancellationToken).ConfigureAwait(false))
            {
                return (new Unavailable("customer backup credential creation was claimed for retirement"), null);
            }

            var retirement = replaced is null ? null : await lifecycle.QueueRetirementAsync(transaction, replaced, cancellationToken).ConfigureAwait(false);
            if (replaced is not null && retirement is null)
            {
                return (new Unavailable("customer backup credential lifecycle backlog is full"), null);
            }

            await lifecycle.CompleteCreateAsync(transaction, record.Revision, cancellationToken).ConfigureAwait(false);
            await EngineManagementService.TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Collection(projectId), Current, record, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false);
            return (record.Destination, retirement);
        }, cancellationToken).ConfigureAwait(false);

    public async Task<DeleteBackupDestinationResult> DeleteAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken)
    {
        var (result, retirement) = await catalog.WriteDurableAsync<(DeleteBackupDestinationResult, BackupSecretWork?)>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (access != ManagementError.None)
            {
                return (access == ManagementError.Forbidden ? ManagementErrors.RoleTooLow : new NotFound(), null);
            }

            var removed = await GetAsync(transaction, Collection(projectId), Current, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false);
            BackupSecretWork? queued = null;
            if (removed is not null)
            {
                queued = await lifecycle.QueueRetirementAsync(transaction, removed, cancellationToken).ConfigureAwait(false);
                if (queued is null)
                {
                    return (new Unavailable("customer backup credential lifecycle backlog is full"), null);
                }

                await EngineManagementService.TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
                await transaction.DeleteAsync(Collection(projectId), Current, cancellationToken).ConfigureAwait(false);
            }

            return (new Done(), queued);
        }, cancellationToken).ConfigureAwait(false);
        await lifecycle.RetireCommittedAsync(retirement).ConfigureAwait(false);

        return result;
    }

    // Recheck both the project and membership in the transaction that changes its configuration.
    private static async ValueTask<ManagementError> WritableAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await EngineManagementService.GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null
            ? ManagementError.NotFound
            : await EngineManagementService.WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);

    private static async Task ProbeAsync(S3ObjectStore bucket, string organizationId, string projectId, CancellationToken cancellationToken)
    {
        var prefix = BackupLayout.Data($"{organizationId}/{projectId}");
        var key = $"{prefix}.probe/{Ulid.NewUlid()}";
        var expected = Encoding.ASCII.GetBytes("TerraBase backup access check");
        using var content = new MemoryStream(expected);
        try
        {
            await bucket.PutAsync(key, content, cancellationToken).ConfigureAwait(false);
            if (!await bucket.ReadAsync(key, async (stream, readToken) =>
            {
                var received = new byte[expected.Length + 1];
                var length = await stream.ReadAtLeastAsync(received, received.Length, throwOnEndOfStream: false, readToken).ConfigureAwait(false);
                if (length != expected.Length || !received.AsSpan(0, length).SequenceEqual(expected))
                {
                    throw new IOException("customer bucket returned an invalid access check");
                }
            }, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("customer bucket cannot read the access check");
            }

            await using var listed = bucket.ListAsync(prefix, null, cancellationToken).GetAsyncEnumerator(cancellationToken);
            _ = await listed.MoveNextAsync().ConfigureAwait(false);
        }
        finally
        {
            // An object lock may retain this tiny probe; it contains no database or secret material.
            await bucket.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }
}
