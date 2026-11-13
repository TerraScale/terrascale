using System.Security.Cryptography;
using Amazon.S3;

namespace TerraBase.Features.Backup;

internal sealed class KeySnapshotInventory(IKeyring keyring, TimeProvider time, S3ObjectStore? bucket = null, DataKeys? keys = null)
{
    internal async Task<IReadOnlyDictionary<string, double>> RunAsync(KeyInventory inventory, CancellationToken cancellationToken)
    {
        var ages = keyring.KeyIds.ToDictionary(id => id, _ => 0d, StringComparer.Ordinal);
        if (bucket is null || keys is null)
        {
            return ages;
        }
        var unreadable = false;
        await foreach (var item in bucket.RetainedSnapshotsAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var snapshot = BackupLayout.ParseSnapshot(item.Key) ?? throw new InvalidDataException("snapshot name is malformed");
                var wrapped = await bucket.SnapshotWrappedKeyAsync(item.Key, item.Version, keys, cancellationToken).ConfigureAwait(false);
                if (wrapped is null)
                {
                    continue;
                }
                var key = keyring.Unwrap(wrapped);
                CryptographicOperations.ZeroMemory(key);
                var id = keyring.KeyId(wrapped);
                ages[id] = Math.Max(ages[id], Math.Max(1, (time.GetUtcNow() - snapshot.At).TotalSeconds));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or AmazonS3Exception or HttpRequestException or KeyNotFoundException)
            {
                inventory.Unreadable("snapshot/" + item.Version + "/" + item.Key);
                unreadable = true;
            }
        }
        return unreadable ? keyring.KeyIds.ToDictionary(id => id, _ => -1d, StringComparer.Ordinal) : ages;
    }
}
