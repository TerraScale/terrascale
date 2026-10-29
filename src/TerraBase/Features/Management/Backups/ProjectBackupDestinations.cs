using System.Diagnostics;
using System.Text;
using Amazon.S3;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Backups;

internal sealed class ProjectBackupDestinations(
    SystemCatalog catalog, TerraBaseOptions options, IHttpClientFactory http, TimeProvider time, IHostApplicationLifetime lifetime, Lazy<ICatalogGate> gate,
    IMasterKey? master = null) : IProjectBackupDestinations
{
    internal static string Collection(string projectId) => ProjectState(projectId) + "backup_destination";
    private const string Current = "current";
    private const int MaxSaves = 4;
    private readonly Lock _savingLock = new();
    private readonly HashSet<string> _saving = [with(StringComparer.Ordinal)];

    private static readonly NotFound NotSet = new("no backup destination is set for this project; set one with setBackupDestination");

    public ValueTask<ReadBackupDestinationResult> ReadAsync(string organizationId, string projectId, CancellationToken cancellationToken) =>
        catalog.ReadAsync<ReadBackupDestinationResult>(async transaction =>
            await GetAsync(transaction, Collection(projectId), Current, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false)
                is { } record ? record : NotSet, static error => error, cancellationToken);

    public async Task<SaveBackupDestinationResult> SaveAsync(
        string userId, string organizationId, string projectId, ProjectBackupDestinationRequest request, CancellationToken cancellationToken)
    {
        var access = await catalog.ReadAsync(transaction => WritableAsync(transaction, userId, organizationId, projectId, cancellationToken),
            static error => (WritableProject)error, cancellationToken).ConfigureAwait(false);
        if (access is not Project)
        {
            return access switch
            {
                NotFound error => error,
                Forbidden error => error,
                Unavailable error => error,
                Project or null => throw new UnreachableException(),
            };
        }

        if (options.Backup is null || master is null)
        {
            return new Unavailable("customer backup is not configured");
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_savingLock)
        {
            if (_saving.Count == MaxSaves || !_saving.Add(organizationId))
            {
                return new Unavailable("customer backup configuration request limit reached");
            }
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            using (var bucket = new S3ObjectStore(new Uri(request.Endpoint!), request.Region!, request.Bucket!, request.AccessKeyId!, request.SecretKey!, http,
                S3ObjectStore.CustomerHttpClientName, time))
            {
                await ProbeAsync(bucket, organizationId, projectId, timeout.Token).ConfigureAwait(false);
            }

            var credentials = await CustomerCredentials.SealAsync(master, request.AccessKeyId!, request.SecretKey!, timeout.Token).ConfigureAwait(false);
            var destination = new ProjectBackupDestination(new Uri(request.Endpoint!), request.Region!, request.Bucket!, request.Mode!, time.GetUtcNow());
            return await SaveCoreAsync(userId, organizationId, projectId, new ProjectBackupDestinationRecord(1, Ulid.NewUlid().ToString(), destination, credentials), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or AmazonS3Exception or IOException or InvalidDataException or OperationCanceledException)
        {
            return new Unavailable("customer backup bucket could not be accessed");
        }
        finally
        {
            lock (_savingLock)
            {
                _saving.Remove(organizationId);
            }
        }
    }

    private async Task<SaveBackupDestinationResult> SaveCoreAsync(
        string userId, string organizationId, string projectId, ProjectBackupDestinationRecord record, CancellationToken cancellationToken)
    {
        var replaced = false;
        var saved = await catalog.WriteDurableAsync<SaveBackupDestinationResult>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (access is not Project)
            {
                return access switch
                {
                    NotFound error => (SaveBackupDestinationResult)error,
                    Forbidden error => error,
                    Unavailable error => error,
                    Project or null => throw new UnreachableException(),
                };
            }

            if (await ProjectClosure.IsClosedAsync(transaction, projectId, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NotFound;
            }

            var previous = await GetAsync(transaction, Collection(projectId), Current, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false);
            replaced = previous is not null;
            if (previous is not null)
            {
                await BackupDestinationHistory.SaveAsync(transaction, projectId, previous, cancellationToken).ConfigureAwait(false);
            }
            await BackupDestinationHistory.SaveAsync(transaction, projectId, record, cancellationToken).ConfigureAwait(false);
            await EngineManagementService.TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Collection(projectId), Current, record, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false);
            return record.Destination;
        }, static error => error, cancellationToken).ConfigureAwait(false);

        return saved is ProjectBackupDestination && replaced && !await gate.Value.TryAnchorAsync(cancellationToken).ConfigureAwait(false)
            ? CatalogGate.AnchorUnavailable : saved;
    }

    public async Task<DeleteBackupDestinationResult> DeleteAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await catalog.WriteDurableAsync<DeleteBackupDestinationResult>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (access is not Project)
            {
                return access switch
                {
                    NotFound error => (DeleteBackupDestinationResult)error,
                    Forbidden error => error,
                    Unavailable error => error,
                    Project or null => throw new UnreachableException(),
                };
            }

            if (await GetAsync(transaction, Collection(projectId), Current, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, cancellationToken).ConfigureAwait(false) is { } previous)
            {
                await BackupDestinationHistory.SaveAsync(transaction, projectId, previous, cancellationToken).ConfigureAwait(false);
                await EngineManagementService.TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
                await transaction.DeleteAsync(Collection(projectId), Current, cancellationToken).ConfigureAwait(false);
            }

            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);

    private static async ValueTask<WritableProject> WritableAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken)
    {
        if (await EngineManagementService.GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not { } project)
        {
            return new NotFound();
        }

        return await EngineManagementService.WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) switch
        {
            OrganizationMember => project,
            Forbidden => ManagementErrors.RoleTooLow,
            NotFound => new NotFound(),
        };
    }

    private async Task ProbeAsync(S3ObjectStore bucket, string organizationId, string projectId, CancellationToken cancellationToken)
    {
        var prefix = BackupLayout.Data($"{organizationId}/{projectId}");
        var key = $"{prefix}.probe/{Ulid.NewUlid()}";
        var expected = Encoding.ASCII.GetBytes("TerraBase backup access check");
        using var content = new MemoryStream(expected);
        var verified = false;
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

            await using (var listed = bucket.ListAsync(prefix, null, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                _ = await listed.MoveNextAsync().ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            verified = true;
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, lifetime.ApplicationStopping);
            try
            {
                await bucket.DeleteAsync(key, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!verified)
            {
            }
        }
    }
}
