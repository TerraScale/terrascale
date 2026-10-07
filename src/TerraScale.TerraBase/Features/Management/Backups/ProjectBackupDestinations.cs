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
    SystemCatalog catalog, TerraBaseOptions options, IHttpClientFactory http, TimeProvider time, IOciVaultSecrets? secrets = null) : IProjectBackupDestinations, IDisposable
{
    private static string Collection(string projectId) => ProjectState(projectId) + "backup_destination";
    private const string Current = "current";
    private readonly SemaphoreSlim _saving = new(4, 4);

    public void Dispose() => _saving.Dispose();

    public ValueTask<ProjectBackupDestinationRecord?> ReadAsync(string organizationId, string projectId, CancellationToken cancellationToken) =>
        catalog.ReadAsync(transaction => GetAsync(transaction, Collection(projectId), Current, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken), cancellationToken);

    public async Task<SaveBackupDestinationResult> SaveAsync(
        string userId, string organizationId, string projectId, ProjectBackupDestinationRequest request, CancellationToken cancellationToken)
    {
        if (!await _saving.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new Unavailable("customer backup configuration request limit reached");
        }

        try
        {
            return await SaveCoreAsync(userId, organizationId, projectId, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saving.Release();
        }
    }

    private async Task<SaveBackupDestinationResult> SaveCoreAsync(
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

        string? secretId = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using (var bucket = new S3ObjectStore(new Uri(request.Endpoint!), request.Region!, request.Bucket!, request.AccessKeyId!, request.SecretKey!, http,
                S3ObjectStore.CustomerHttpClientName, time))
            {
                await ProbeAsync(bucket, organizationId, projectId, timeout.Token).ConfigureAwait(false);
            }

            secretId = await secrets.CreateAsync("terrabase-backup-" + Guid.NewGuid().ToString("N"), request.AccessKeyId!, request.SecretKey!, timeout.Token).ConfigureAwait(false);
            var destination = new ProjectBackupDestination(new Uri(request.Endpoint!), request.Region!, request.Bucket!, request.Mode!, time.GetUtcNow());
            var record = new ProjectBackupDestinationRecord(1, Guid.NewGuid().ToString("N"), destination, secretId);
            var saved = await catalog.WriteAsync<SaveBackupDestinationResult>(async transaction =>
            {
                var access = await WritableAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
                if (access != ManagementError.None)
                {
                    return access == ManagementError.Forbidden ? ManagementErrors.RoleTooLow : new NotFound();
                }

                await PutAsync(transaction, Collection(projectId), Current, record, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false);
                return destination;
            }, cancellationToken).ConfigureAwait(false);
            if (saved is ProjectBackupDestination)
            {
                secretId = null;
            }

            return saved;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or AmazonS3Exception or IOException or OperationCanceledException)
        {
            // Bucket/Vault errors can include tenant input and credentials. Only a fixed error reaches HTTP.
            return new Unavailable("customer backup bucket or OCI Vault could not be accessed");
        }
        finally
        {
            if (secretId is not null)
            {
                await RetireAsync(secretId).ConfigureAwait(false);
            }
        }
    }

    public async Task<DeleteBackupDestinationResult> DeleteAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await catalog.WriteAsync<DeleteBackupDestinationResult>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (access != ManagementError.None)
            {
                return access == ManagementError.Forbidden ? ManagementErrors.RoleTooLow : new NotFound();
            }

            await transaction.DeleteAsync(Collection(projectId), Current, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, cancellationToken).ConfigureAwait(false);

    // Recheck both the project and membership in the transaction that changes its configuration.
    private static async ValueTask<ManagementError> WritableAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await EngineManagementService.GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null
            ? ManagementError.NotFound
            : await EngineManagementService.WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);

    private async Task RetireAsync(string id)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await secrets!.DeleteAsync(id, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            // A failed save never publishes the reference; Vault operators can remove the orphan by its name.
        }
    }

    private static async Task ProbeAsync(S3ObjectStore bucket, string organizationId, string projectId, CancellationToken cancellationToken)
    {
        var key = $"v1/{organizationId}/{projectId}/.probe/{Guid.NewGuid():N}";
        using var content = new MemoryStream(Encoding.ASCII.GetBytes("TerraBase backup access check"));
        try
        {
            await bucket.PutAsync(key, content, cancellationToken).ConfigureAwait(false);
            if (!await bucket.ReadAsync(key, (stream, readToken) => stream.CopyToAsync(Stream.Null, readToken), cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("customer bucket cannot read the access check");
            }

            await using var listed = bucket.ListAsync($"v1/{organizationId}/{projectId}/", null, cancellationToken).GetAsyncEnumerator(cancellationToken);
            _ = await listed.MoveNextAsync().ConfigureAwait(false);
        }
        finally
        {
            // An object lock may retain this tiny probe; it contains no database or secret material.
            await bucket.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }
}
