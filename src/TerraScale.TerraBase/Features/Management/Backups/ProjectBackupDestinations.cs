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
    SystemCatalog catalog, TerraBaseOptions options, IHttpClientFactory http, TimeProvider time, IOciVaultSecrets? secrets = null) : IProjectBackupDestinations
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

        string? secretId = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using (var bucket = new S3ObjectStore(new Uri(request.Endpoint!), request.Region!, request.Bucket!, request.AccessKeyId!, request.SecretKey!, http,
                S3ObjectStore.CustomerHttpClientName, time))
            {
                await ProbeAsync(bucket, organizationId, projectId, timeout.Token).ConfigureAwait(false);
            }

            secretId = await secrets.CreateAsync("terrabase-backup-" + Ulid.NewUlid().ToString(), request.AccessKeyId!, request.SecretKey!, timeout.Token).ConfigureAwait(false);
            var destination = new ProjectBackupDestination(new Uri(request.Endpoint!), request.Region!, request.Bucket!, request.Mode!, time.GetUtcNow());
            var record = new ProjectBackupDestinationRecord(1, Ulid.NewUlid().ToString(), destination, secretId);
            var (saved, retiredSecretId) = await SaveCoreAsync(userId, organizationId, projectId, record, timeout.Token).ConfigureAwait(false);
            if (saved is ProjectBackupDestination)
            {
                // WriteAsync has committed: the new secret is current, and only its predecessor can retire.
                secretId = retiredSecretId;
            }

            return saved;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or AmazonS3Exception or IOException or InvalidDataException or OperationCanceledException)
        {
            // Bucket/Vault errors can include tenant input and credentials. Only a fixed error reaches HTTP.
            return new Unavailable("customer backup bucket or OCI Vault could not be accessed");
        }
        finally
        {
            lock (_savingLock)
            {
                _saving.Remove(organizationId);
            }

            if (secretId is not null)
            {
                await RetireAsync(secretId).ConfigureAwait(false);
            }
        }
    }

    private async Task<(SaveBackupDestinationResult Result, string? RetiredSecretId)> SaveCoreAsync(
        string userId, string organizationId, string projectId, ProjectBackupDestinationRecord record, CancellationToken cancellationToken) =>
        await catalog.WriteAsync<(SaveBackupDestinationResult, string?)>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (access != ManagementError.None)
            {
                return (access == ManagementError.Forbidden ? ManagementErrors.RoleTooLow : new NotFound(), null);
            }

            var replaced = await GetAsync(transaction, Collection(projectId), Current, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false);
            await EngineManagementService.TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Collection(projectId), Current, record, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false);
            return (record.Destination, replaced?.SecretId);
        }, cancellationToken).ConfigureAwait(false);

    public async Task<DeleteBackupDestinationResult> DeleteAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken)
    {
        var (result, retiredSecretId) = await catalog.WriteAsync<(DeleteBackupDestinationResult, string?)>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (access != ManagementError.None)
            {
                return (access == ManagementError.Forbidden ? ManagementErrors.RoleTooLow : new NotFound(), null);
            }

            var removed = await GetAsync(transaction, Collection(projectId), Current, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false);
            if (removed is not null)
            {
                await EngineManagementService.TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
                await transaction.DeleteAsync(Collection(projectId), Current, cancellationToken).ConfigureAwait(false);
            }

            return (new Done(), removed?.SecretId);
        }, cancellationToken).ConfigureAwait(false);
        if (retiredSecretId is not null)
        {
            await RetireAsync(retiredSecretId).ConfigureAwait(false);
        }

        return result;
    }

    // Recheck both the project and membership in the transaction that changes its configuration.
    private static async ValueTask<ManagementError> WritableAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await EngineManagementService.GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null
            ? ManagementError.NotFound
            : await EngineManagementService.WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);

    private async Task RetireAsync(string id)
    {
        if (secrets is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await secrets.DeleteAsync(id, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            // Retirement is best effort; failures must not turn a committed configuration change into an error.
        }
    }

    private static async Task ProbeAsync(S3ObjectStore bucket, string organizationId, string projectId, CancellationToken cancellationToken)
    {
        var prefix = BackupLayout.Data($"{organizationId}/{projectId}");
        var key = $"{prefix}.probe/{Ulid.NewUlid()}";
        using var content = new MemoryStream(Encoding.ASCII.GetBytes("TerraBase backup access check"));
        try
        {
            await bucket.PutAsync(key, content, cancellationToken).ConfigureAwait(false);
            if (!await bucket.ReadAsync(key, (stream, readToken) => stream.CopyToAsync(Stream.Null, readToken), cancellationToken).ConfigureAwait(false))
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
