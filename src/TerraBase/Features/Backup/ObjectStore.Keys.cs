using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

namespace TerraBase.Features.Backup;

internal sealed partial class S3ObjectStore
{
    internal async IAsyncEnumerable<(string Key, string Version)> RetainedSnapshotsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var versions = new List<string> { string.Empty };
        if (await SnapshotsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.AddRange((await ListSnapshotsAsync(cancellationToken).ConfigureAwait(false)).Select(snapshot => snapshot.Version));
        }
        foreach (var version in versions)
        {
            var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = "v1/", MaxKeys = 1000 };
            if (version.Length > 0)
            {
                request = Tigris(request, "X-Tigris-Snapshot-Version", version);
            }
            while (true)
            {
                var page = await SafeAsync(_s3.ListObjectsV2Async(request, cancellationToken)).ConfigureAwait(false);
                foreach (var item in page.S3Objects ?? [])
                {
                    if (item.Key is { } key && key.StartsWith("v1/", StringComparison.Ordinal) && key.Contains("/snapshots/", StringComparison.Ordinal))
                    {
                        yield return (key, version);
                    }
                }
                if (page.IsTruncated != true)
                {
                    break;
                }
                if (string.IsNullOrEmpty(page.NextContinuationToken) || page.NextContinuationToken == request.ContinuationToken)
                {
                    throw new InvalidDataException("snapshot inventory listing did not advance");
                }
                request.ContinuationToken = page.NextContinuationToken;
            }
        }
    }

    internal async Task<string?> SnapshotWrappedKeyAsync(string path, string version, DataKeys keys, CancellationToken cancellationToken)
    {
        var boundary = path.LastIndexOf("/nodes/", StringComparison.Ordinal);
        if (boundary < 3)
        {
            throw new InvalidDataException("snapshot scope is malformed");
        }
        var scope = path[3..boundary];
        var request = new GetObjectRequest { BucketName = _bucket, Key = path, ByteRange = new ByteRange(0, 2 * 1024 * 1024 - 1) };
        if (version.Length > 0)
        {
            request = Tigris(request, "X-Tigris-Snapshot-Version", version);
        }
        try
        {
            using var response = await SafeAsync(_s3.GetObjectAsync(request, cancellationToken)).ConfigureAwait(false);
            return await BackupObject.SnapshotWrappedKeyAsync(response.ResponseStream, path, id => keys.GetAsync(scope, id, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound && error.ErrorCode != "NoSuchBucket")
        {
            return null;
        }
    }

    internal async Task<string?> RewrapKeyAsync(string path, IKeyring keyring, CancellationToken cancellationToken)
    {
        GetObjectResponse response;
        try
        {
            response = await SafeAsync(_s3.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = path }, cancellationToken)).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound && error.ErrorCode != "NoSuchBucket")
        {
            return null;
        }

        using (response)
        {
            var bytes = new byte[16 * 1024 + 1];
            var length = await response.ResponseStream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (length is 0 or > 16 * 1024 || string.IsNullOrEmpty(response.ETag))
            {
                throw new InvalidDataException("custody wrapped key or entity tag is malformed");
            }
            var wrapped = Encoding.ASCII.GetString(bytes, 0, length);
            var key = keyring.Unwrap(wrapped);
            try
            {
                var id = keyring.KeyId(wrapped);
                if (!keyring.NeedsRewrap(wrapped))
                {
                    return id;
                }
                using var content = new MemoryStream(Encoding.ASCII.GetBytes(keyring.Wrap(key)));
                try
                {
                    await SafeAsync(_s3.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = _bucket,
                        Key = path,
                        InputStream = content,
                        AutoCloseStream = false,
                        UseChunkEncoding = false,
                        IfMatch = response.ETag,
                        Headers = { CacheControl = "no-store" },
                    }, cancellationToken)).ConfigureAwait(false);
                    return keyring.ActiveId;
                }
                catch (AmazonS3Exception error) when (error.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound
                    && error.ErrorCode != "NoSuchBucket")
                {
                    return id;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }
}
