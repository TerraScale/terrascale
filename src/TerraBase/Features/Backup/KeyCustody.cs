using TerraBase.Configuration;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

internal sealed class KeyCustody(ShapeStorageSettings settings, IHttpClientFactory http, TimeProvider time) : IDisposable
{
    internal KeyCacheRevocations Revocations { get; } = new();

    public S3ObjectStore Store { get; } = new(settings.Endpoint, settings.Region, settings.Bucket, settings.AccessKeyId,
        settings.SecretKey, http, Shapes.ShapeObjectStore.HttpClientName, time);

    public async Task<(ShredRecord Record, bool Absent)> ShredAsync(TenantContext database, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var scope = BackupLayout.KeyScope(database);
        var record = await ShredRecords.ReadAsync(Store, scope, cancellationToken).ConfigureAwait(false)
            ?? new ShredRecord(database.DatabaseId!, now);
        await ShredRecords.WriteAsync(Store, scope, record, cancellationToken).ConfigureAwait(false);
        Revocations.Forget(scope);
        var prefix = BackupLayout.Keys(scope);
        var marker = BackupLayout.Shredded(scope);
        var complete = true;
        var count = 0;
        await foreach (var item in Store.ListAsync(prefix, null, cancellationToken).ConfigureAwait(false))
        {
            if (++count > 8192)
            {
                return (record, false);
            }
            if (item.Key.Length > 0 && item.Key != marker)
            {
                complete &= await Store.DeleteAsync(item.Key, cancellationToken).ConfigureAwait(false);
                complete &= !await Store.ReadAsync(item.Key, static (_, _) => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
            }
        }
        await foreach (var item in Store.ListAsync(prefix, null, cancellationToken).ConfigureAwait(false))
        {
            if (item.Key.Length > 0 && item.Key != marker)
            {
                return (record, false);
            }
        }
        return (record, complete);
    }

    public void Dispose() => Store.Dispose();
}
