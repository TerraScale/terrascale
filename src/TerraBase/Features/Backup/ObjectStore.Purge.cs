using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

namespace TerraBase.Features.Backup;

internal sealed record PrefixPurgeProgress(string? VersionKey = null, string? VersionId = null, bool VersionsDirty = false,
    string? UploadKey = null, string? UploadId = null, bool UploadsDirty = false);

internal sealed record PrefixPurgeResult(PrefixPurgeProgress Progress, bool Absent);

internal sealed partial class S3ObjectStore
{
    // Called only after durable deletion authority has stopped every writer to this database prefix.
    // A fresh empty versions AND uploads inventory is the proof; DELETE acknowledgements alone are insufficient.
    public async Task<PrefixPurgeResult> PurgePrefixAsync(string prefix, PrefixPurgeProgress progress, CancellationToken cancellationToken)
    {
        var parts = prefix.Split('/');
        if (parts is not ["v1", var organization, var project, var database, ""]
            || !Ulid.TryParse(organization, out _) || !Ulid.TryParse(project, out _) || !Ulid.TryParse(database, out _))
        {
            throw new InvalidDataException("database purge prefix is invalid");
        }

        const int batch = 128;
        var versions = await SafeAsync(_s3.ListVersionsAsync(new ListVersionsRequest
        {
            BucketName = _bucket,
            Prefix = prefix,
            MaxKeys = batch,
            KeyMarker = progress.VersionKey,
            VersionIdMarker = progress.VersionId,
        }, cancellationToken)).ConfigureAwait(false);
        var objects = versions.Versions ?? [];
        if (objects.Count > batch || objects.Any(item => !ValidKey(item.Key, prefix)
            || string.IsNullOrEmpty(item.VersionId) || item.VersionId.Length > 1024))
        {
            throw new InvalidDataException("backup version inventory is invalid");
        }

        var uploads = await SafeAsync(_s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
        {
            BucketName = _bucket,
            Prefix = prefix,
            MaxUploads = batch,
            KeyMarker = progress.UploadKey,
            UploadIdMarker = progress.UploadId,
        }, cancellationToken)).ConfigureAwait(false);
        var pending = uploads.MultipartUploads ?? [];
        if (pending.Count > batch || pending.Any(item => !ValidKey(item.Key, prefix)
            || string.IsNullOrEmpty(item.UploadId) || item.UploadId.Length > 1024))
        {
            throw new InvalidDataException("backup multipart inventory is invalid");
        }

        if (versions.IsTruncated == true && (!ValidKey(versions.NextKeyMarker, prefix)
            || string.IsNullOrEmpty(versions.NextVersionIdMarker) || versions.NextVersionIdMarker.Length > 1024
            || (versions.NextKeyMarker == progress.VersionKey && versions.NextVersionIdMarker == progress.VersionId))
            || uploads.IsTruncated == true && (!ValidKey(uploads.NextKeyMarker, prefix)
            || string.IsNullOrEmpty(uploads.NextUploadIdMarker) || uploads.NextUploadIdMarker.Length > 1024
            || (uploads.NextKeyMarker == progress.UploadKey && uploads.NextUploadIdMarker == progress.UploadId)))
        {
            throw new InvalidDataException("backup purge continuation is invalid");
        }

        var refused = false;
        foreach (var item in objects)
        {
            try
            {
                await SafeAsync(_s3.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = _bucket,
                    Key = item.Key,
                    VersionId = item.VersionId,
                }, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                refused = true;
                break; // Persist the listed page's continuation even when its mutation budget expires.
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is AmazonS3Exception or HttpRequestException)
            {
                refused = true;
            }
        }

        foreach (var upload in pending)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                refused = true;
                break;
            }

            try
            {
                await SafeAsync(_s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = _bucket,
                    Key = upload.Key,
                    UploadId = upload.UploadId,
                }, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                refused = true;
                break; // Persist the listed page's continuation even when its mutation budget expires.
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is AmazonS3Exception or HttpRequestException)
            {
                refused = true;
            }
        }

        var versionsDirty = progress.VersionsDirty || objects.Count > 0;
        var uploadsDirty = progress.UploadsDirty || pending.Count > 0;
        var next = new PrefixPurgeProgress(
            versions.IsTruncated == true ? versions.NextKeyMarker : null,
            versions.IsTruncated == true ? versions.NextVersionIdMarker : null,
            versions.IsTruncated == true && versionsDirty,
            uploads.IsTruncated == true ? uploads.NextKeyMarker : null,
            uploads.IsTruncated == true ? uploads.NextUploadIdMarker : null,
            uploads.IsTruncated == true && uploadsDirty);
        if (refused || versionsDirty || uploadsDirty || versions.IsTruncated == true || uploads.IsTruncated == true)
        {
            return new(next, false); // Finish this sweep before revisiting retained entries from its beginning.
        }

        // Detect incompatible version-list implementations that omit current objects instead of certifying absence.
        await using var listed = ListAsync(prefix, null, cancellationToken).GetAsyncEnumerator(cancellationToken);
        return new(next, !await listed.MoveNextAsync().ConfigureAwait(false));
    }

    private static bool ValidKey(string? key, string prefix) =>
        key is { Length: > 0 and <= 1024 } && Encoding.UTF8.GetByteCount(key) <= 1024 && key.StartsWith(prefix, StringComparison.Ordinal)
            && !key.Split('/').Any(part => part is "." or "..");
}
