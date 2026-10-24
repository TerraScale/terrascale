using Amazon.S3.Model;

namespace TerraBase.Features.Backup;

internal sealed partial class S3ObjectStore
{
    /// <summary>
    /// Whether <paramref name="prefix"/> may still hold backup data from before <paramref name="cut"/> that a current-key listing
    /// misses (TS-873 history erasure): a noncurrent version or delete marker (backup keys are written once, so any is a
    /// removed or replaced object a versioned bucket keeps), or a multipart upload begun before the cut. True when the
    /// inventory exceeds <paramref name="maxItems"/> or makes no progress: unproven is held.
    /// </summary>
    public async Task<bool> HoldsHiddenVersionsAsync(string prefix, DateTimeOffset cut, int maxItems, CancellationToken cancellationToken)
    {
        var seen = 0;
        string? key = null;
        string? version = null;
        while (true)
        {
            var page = await SafeAsync(_s3.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = _bucket,
                Prefix = prefix,
                MaxKeys = 128,
                KeyMarker = key,
                VersionIdMarker = version,
            }, cancellationToken)).ConfigureAwait(false);
            foreach (var item in page.Versions ?? [])
            {
                if (++seen > maxItems || item.IsLatest != true || item.IsDeleteMarker == true)
                {
                    return true;
                }
            }

            if (page.IsTruncated != true)
            {
                break;
            }

            if (page.NextKeyMarker == key && page.NextVersionIdMarker == version)
            {
                return true;
            }

            (key, version) = (page.NextKeyMarker, page.NextVersionIdMarker);
        }

        string? uploadKey = null;
        string? uploadId = null;
        while (true)
        {
            var uploads = await SafeAsync(_s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
            {
                BucketName = _bucket,
                Prefix = prefix,
                MaxUploads = 128,
                KeyMarker = uploadKey,
                UploadIdMarker = uploadId,
            }, cancellationToken)).ConfigureAwait(false);
            foreach (var upload in uploads.MultipartUploads ?? [])
            {
                if (++seen > maxItems || upload.Initiated is not { } initiated || new DateTimeOffset(initiated.ToUniversalTime(), TimeSpan.Zero) < cut)
                {
                    return true;
                }
            }

            if (uploads.IsTruncated != true)
            {
                return false;
            }

            if (uploads.NextKeyMarker == uploadKey && uploads.NextUploadIdMarker == uploadId)
            {
                return true;
            }

            (uploadKey, uploadId) = (uploads.NextKeyMarker, uploads.NextUploadIdMarker);
        }
    }
}
