using System.Text.Json;
using Amazon.S3.Model;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

/// <summary>The deletion record a shredded database's key scope keeps: which database, and when its keys were destroyed.</summary>
internal sealed record ShredRecord(string DatabaseId, DateTimeOffset ShreddedAt);

/// <summary>A deleted database's data keys are destroyed: none of its backup objects or shape chunks can be decrypted.</summary>
internal sealed class DatabaseShreddedException(DateTimeOffset? shreddedAt)
    : IOException("the database was deleted and its backup data keys destroyed")
{
    public DateTimeOffset? ShreddedAt { get; } = shreddedAt;
}

internal static class ShredRecords
{
    private const int MaxBytes = 4096;

    /// <summary>The deletion record of <paramref name="scope"/>, or <see langword="null"/> when it was never shredded.</summary>
    public static async Task<ShredRecord?> ReadAsync(IObjectStore store, string scope, CancellationToken cancellationToken)
    {
        ShredRecord? record = null;
        var found = await store.ReadAsync(BackupLayout.Shredded(scope), async (content, token) =>
        {
            var buffer = new byte[MaxBytes + 1];
            var length = await content.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
            if (length > MaxBytes)
            {
                throw new InvalidDataException("a backup deletion record exceeds its byte limit");
            }

            try
            {
                record = JsonSerializer.Deserialize(buffer.AsSpan(0, length), BackupPurgeJsonContext.Default.ShredRecord);
            }
            catch (JsonException)
            {
                record = null;
            }
        }, cancellationToken).ConfigureAwait(false);
        // A damaged record still means shredded: its presence, not its content, refuses keys.
        return found ? record ?? new ShredRecord("", DateTimeOffset.MinValue) : null;
    }

    public static async Task WriteAsync(IObjectStore store, string scope, ShredRecord record, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(record, BackupPurgeJsonContext.Default.ShredRecord));
        await store.PutAsync(BackupLayout.Shredded(scope), content, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed partial class S3ObjectStore
{
    private const int ShredPages = 64;

    /// <summary>
    /// Crypto-shreds <paramref name="database"/> in this bucket: writes its deletion record first, so no member selects or
    /// generates a key for it again, then deletes every version of its wrapped data keys and of the project-wide keys that
    /// preceded per-database scopes. Old snapshots and segments stay as they are; without their keys they cannot be read.
    /// </summary>
    /// <returns>The deletion record kept, and whether no key version remains.</returns>
    public async Task<(ShredRecord Record, bool Absent)> ShredKeysAsync(TenantContext database, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(database.ProjectId);
        ArgumentException.ThrowIfNullOrEmpty(database.DatabaseId);
        var scope = BackupLayout.KeyScope(database);
        var record = await ShredRecords.ReadAsync(this, scope, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            record = new ShredRecord(database.DatabaseId, now);
            await ShredRecords.WriteAsync(this, scope, record, cancellationToken).ConfigureAwait(false);
        }

        var marker = BackupLayout.Shredded(scope);
        var keys = BackupLayout.Keys(scope);
        var project = BackupLayout.ProjectKeys(database);
        var absent = await DeleteKeyVersionsAsync(keys, key => key != marker, cancellationToken).ConfigureAwait(false);
        absent &= await DeleteKeyVersionsAsync(project, key => key.IndexOf('/', project.Length) < 0
            && Ulid.TryParse(key[project.Length..], out _), cancellationToken).ConfigureAwait(false);
        return (record, absent);
    }

    private async Task<bool> DeleteKeyVersionsAsync(string prefix, Func<string, bool> selected, CancellationToken cancellationToken)
    {
        string? keyMarker = null;
        string? versionMarker = null;
        var refused = false;
        for (var page = 0; page < ShredPages; page++)
        {
            var versions = await RetriedAsync(() => _s3.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = _bucket,
                Prefix = prefix,
                MaxKeys = 128,
                KeyMarker = keyMarker,
                VersionIdMarker = versionMarker,
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
            var items = (versions.Versions ?? []).Where(item => ValidKey(item.Key, prefix) && selected(item.Key)
                && !string.IsNullOrEmpty(item.VersionId) && item.VersionId.Length <= 1024).ToList();
            refused |= await MutateAsync(items, (item, token) => RetriedAsync(() => _s3.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = item.Key,
                VersionId = item.VersionId,
            }, token), token), cancellationToken).ConfigureAwait(false);
            if (versions.IsTruncated != true)
            {
                break;
            }

            if (!ValidKey(versions.NextKeyMarker, prefix) || (versions.NextKeyMarker == keyMarker && versions.NextVersionIdMarker == versionMarker))
            {
                throw new InvalidDataException("backup key version continuation is invalid");
            }

            (keyMarker, versionMarker) = (versions.NextKeyMarker, string.IsNullOrEmpty(versions.NextVersionIdMarker) ? null : versions.NextVersionIdMarker);
        }

        if (refused)
        {
            return false;
        }

        // A fresh listing is the proof; delete acknowledgements alone are not.
        await foreach (var item in ListAsync(prefix, null, cancellationToken).ConfigureAwait(false))
        {
            if (item.Key.Length > 0 && selected(item.Key))
            {
                return false;
            }
        }

        return true;
    }
}
