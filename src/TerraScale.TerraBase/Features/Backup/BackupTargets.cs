using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

/// <summary>A fixed set of destinations for a generation or restore; a caller owns customer resources.</summary>
internal sealed class BackupTarget(string revision, IObjectStore store, DataKeys keys, BackupReader reader, IReadOnlyList<IObjectStore> retentionStores, IDisposable? customer = null) : IDisposable
{
    public string Revision { get; } = revision;
    public IObjectStore Store { get; } = store;
    public DataKeys Keys { get; } = keys;
    public BackupReader Reader { get; } = reader;
    public IReadOnlyList<IObjectStore> RetentionStores { get; } = retentionStores;

    public void Dispose()
    {
        if (customer is not null)
        {
            Keys.Dispose();
            customer.Dispose();
        }
    }
}

/// <summary>Creates a fixed generation destination from current project configuration, with no credential cache.</summary>
internal sealed class BackupTargets(
    IObjectStore platform, DataKeys platformKeys, BackupReader platformReader, IProjectBackupDestinations destinations,
    IMasterKey master, IWalBackup engine, TerraBaseOptions options, IHttpClientFactory http, TimeProvider time, IOciVaultSecrets? secrets = null)
{
    public BackupTarget Platform { get; } = new(BackupDestinationNames.Platform, platform, platformKeys, platformReader, [platform]);

    public async ValueTask<ProjectBackupDestinationRecord?> ConfigurationAsync(TenantContext? database, CancellationToken cancellationToken) =>
        database is null ? null : await destinations.ReadAsync(database.OrganizationId, database.ProjectId!, cancellationToken).ConfigureAwait(false);

    public Task<BackupTarget> ShippingAsync(TenantContext? database, ProjectBackupDestinationRecord? record, CancellationToken cancellationToken) =>
        record is null ? Task.FromResult(Platform) : CustomerAsync(database!, record, mirror: record.Destination.Mode == "both", freshKey: true, cancellationToken);

    public async Task<BackupTarget?> RestoreAsync(TenantContext database, string destination, CancellationToken cancellationToken)
    {
        if (destination == BackupDestinationNames.Platform)
        {
            return Platform;
        }

        if (destination != BackupDestinationNames.Customer)
        {
            throw new ArgumentException("backup destination is invalid", nameof(destination));
        }

        return await ConfigurationAsync(database, cancellationToken).ConfigureAwait(false) is { } record
            ? await CustomerAsync(database, record, mirror: false, freshKey: false, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task<BackupTarget> CustomerAsync(TenantContext database, ProjectBackupDestinationRecord record, bool mirror, bool freshKey, CancellationToken cancellationToken)
    {
        if (record.Version != 1 || secrets is null)
        {
            throw new InvalidDataException("customer backup destination version or Vault configuration is unavailable");
        }

        var (accessKeyId, secretKey) = await secrets.ReadAsync(record.SecretId, cancellationToken).ConfigureAwait(false);
        var destination = record.Destination;
        var customer = new S3ObjectStore(destination.Endpoint, destination.Region, destination.Bucket, accessKeyId, secretKey, http, S3ObjectStore.CustomerHttpClientName, time);
        var scope = BackupLayout.KeyScope(database);
        var scopedCustomer = new ProjectStore(customer, scope);
        IObjectStore store = mirror ? new MirroredStore(new ProjectStore(platform, scope), scopedCustomer) : scopedCustomer;
        var keys = new DataKeys(store, master, time, options.Backup!, freshKey, platformKeys.ReadSlots);
        return new(record.Revision, store, keys, new BackupReader(engine, store, keys, options), [new ProjectStore(platform, scope), scopedCustomer], customer);
    }

    // Enforce the project boundary on every operation, including keys fabricated by an untrusted S3 listing.
    private sealed class ProjectStore(IObjectStore inner, string scope) : IObjectStore
    {
        private void Check(string key)
        {
            if ((!key.StartsWith(BackupLayout.Data(scope), StringComparison.Ordinal) && !key.StartsWith(BackupLayout.Keys(scope), StringComparison.Ordinal))
                || key.Split('/').Any(part => part is "." or ".."))
            {
                throw new InvalidDataException("backup object is outside the project's namespace");
            }
        }

        public Task PutAsync(string key, Stream content, CancellationToken cancellationToken)
        {
            Check(key);
            return inner.PutAsync(key, content, cancellationToken);
        }

        public Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken)
        {
            Check(key);
            return inner.ReadAsync(key, read, cancellationToken);
        }

        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken)
        {
            Check(prefix);
            return inner.ListAsync(prefix, startAfter, cancellationToken);
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Check(key);
            return inner.DeleteAsync(key, cancellationToken);
        }
    }

    // Acknowledgements cover both destinations. After a partial upload the shipper starts a fresh generation, so
    // retrying never leaves overlapping segments in either bucket. A failed mirror delays this project's shipping;
    // each destination remains independently restorable from the objects it accepted.
    private sealed class MirroredStore(IObjectStore platform, IObjectStore customer) : IObjectStore
    {
        public async Task PutAsync(string key, Stream content, CancellationToken cancellationToken)
        {
            var start = content.Position;
            await platform.PutAsync(key, content, cancellationToken).ConfigureAwait(false);
            content.Position = start;
            await customer.PutAsync(key, content, cancellationToken).ConfigureAwait(false);
        }

        public Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken) => customer.ReadAsync(key, read, cancellationToken);

        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) => customer.ListAsync(prefix, startAfter, cancellationToken);

        public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
        {
            var removed = await customer.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            return removed && await platform.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }
}
