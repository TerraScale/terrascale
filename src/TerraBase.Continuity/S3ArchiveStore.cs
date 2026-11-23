using System.Diagnostics;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace TerraBase.Continuity;

public sealed record ArchiveObjectWritten;

public sealed record ArchiveObjectMissing(string Message, string Code);

public sealed record ArchiveUnavailable(string Message, string Code);

public sealed record ArchiveTimedOut(string Message, string Code);

public readonly union ArchiveWriteOutcome(ArchiveObjectWritten, ArchiveUnavailable, ArchiveTimedOut);

public readonly union ArchiveBytesOutcome(byte[], ArchiveObjectMissing, ArchiveUnavailable, ArchiveTimedOut);

public readonly union ArchiveDownloadOutcome(long, ArchiveObjectMissing, ArchiveUnavailable, ArchiveTimedOut);

public readonly union ArchiveListOutcome(IReadOnlyList<string>, ArchiveUnavailable, ArchiveTimedOut);

internal readonly union ArchiveTransportFailure(ArchiveObjectMissing, ArchiveUnavailable, ArchiveTimedOut);

public sealed class S3ArchiveStore : IDisposable
{
    private const string NoSuchBucket = "NoSuchBucket";

    private static readonly ArchiveUnavailable Disabled = new("continuous backup is disabled on this node", "backup_disabled");

    private readonly AmazonS3Client? _client;
    private readonly string? _bucket;

    public S3ArchiveStore(ContinuityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Archive is not { } archive)
        {
            return;
        }

        _bucket = archive.Bucket;
        _client = new AmazonS3Client(new BasicAWSCredentials(archive.AccessKeyId, archive.SecretAccessKey), new AmazonS3Config
        {
            ServiceURL = archive.Endpoint.ToString(),
            AuthenticationRegion = archive.Region,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromMinutes(5),
        });
    }

    public bool IsEnabled => _client is not null;

    public async ValueTask<ArchiveWriteOutcome> UploadFileAsync(string key, string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var uploaded = await InvokeAsync(
            async (client, bucket, token) =>
            {
                await using var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                _ = await client.PutObjectAsync(Put(bucket, key, content, ManifestCodec.BinaryContentType), token);
            },
            cancellationToken);
        return Narrow(uploaded);
    }

    public async ValueTask<ArchiveWriteOutcome> UploadStreamAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        var uploaded = await InvokeAsync(
            async (client, bucket, token) =>
            {
                _ = await client.PutObjectAsync(Put(bucket, key, content, contentType), token);
            },
            cancellationToken);
        return Narrow(uploaded);
    }

    public async ValueTask<ArchiveWriteOutcome> PutBytesAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        return await UploadStreamAsync(key, stream, contentType, cancellationToken);
    }

    public async ValueTask<ArchiveBytesOutcome> GetBytesAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        byte[]? payload = null;
        var fetched = await InvokeAsync(
            async (client, bucket, token) =>
            {
                using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, token);
                using var buffer = new MemoryStream();
                await response.ResponseStream.CopyToAsync(buffer, token);
                payload = buffer.ToArray();
            },
            cancellationToken);
        if (fetched is { } failure)
        {
            return failure switch
            {
                ArchiveObjectMissing missing => missing,
                ArchiveUnavailable unavailable => unavailable,
                ArchiveTimedOut timeout => timeout,
            };
        }

        if (payload is not { } content)
        {
            throw new UnreachableException();
        }

        return content;
    }

    public async ValueTask<ArchiveDownloadOutcome> DownloadToFileAsync(string key, string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        long? length = null;
        var fetched = await InvokeAsync(
            async (client, bucket, token) =>
            {
                using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, token);
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                await response.ResponseStream.CopyToAsync(file, token);
                await file.FlushAsync(token);
                file.Flush(flushToDisk: true);
                length = file.Length;
            },
            cancellationToken);
        if (fetched is { } failure)
        {
            return failure switch
            {
                ArchiveObjectMissing missing => missing,
                ArchiveUnavailable unavailable => unavailable,
                ArchiveTimedOut timeout => timeout,
            };
        }

        if (length is not { } bytes)
        {
            throw new UnreachableException();
        }

        return bytes;
    }

    public async ValueTask<ArchiveListOutcome> ListKeysAsync(string prefix, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        IReadOnlyList<string>? keys = null;
        var listed = await InvokeAsync(
            async (client, bucket, token) => keys = await ListAllAsync(client, bucket, prefix, token),
            cancellationToken);
        if (listed is { } failure)
        {
            return failure switch
            {
                ArchiveObjectMissing missing => new ArchiveUnavailable(missing.Message, "archive_bucket_missing"),
                ArchiveUnavailable unavailable => unavailable,
                ArchiveTimedOut timeout => timeout,
            };
        }

        if (keys is not { } found)
        {
            throw new UnreachableException();
        }

        return found;
    }

    public async ValueTask<ArchiveWriteOutcome> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var deleted = await InvokeAsync(
            async (client, bucket, token) =>
            {
                _ = await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, token);
            },
            cancellationToken);
        if (deleted is not { } failure)
        {
            return new ArchiveObjectWritten();
        }

        return failure switch
        {
            ArchiveObjectMissing => new ArchiveObjectWritten(),
            ArchiveUnavailable unavailable => unavailable,
            ArchiveTimedOut timeout => timeout,
        };
    }

    public void Dispose() => _client?.Dispose();

    private static async Task<IReadOnlyList<string>> ListAllAsync(AmazonS3Client client, string bucket, string prefix, CancellationToken cancellationToken)
    {
        List<string> keys = [];
        string? continuation = null;
        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, ContinuationToken = continuation },
                cancellationToken);
            if (response.S3Objects is { } objects)
            {
                keys.AddRange(objects.Select(static item => item.Key));
            }

            continuation = response.IsTruncated is true ? response.NextContinuationToken : null;
        }
        while (continuation is not null);

        return keys;
    }

    private static PutObjectRequest Put(string bucket, string key, Stream content, string contentType) => new()
    {
        BucketName = bucket,
        Key = key,
        InputStream = content,
        AutoCloseStream = false,
        UseChunkEncoding = false,
        ContentType = contentType,
    };

    private static ArchiveWriteOutcome Narrow(ArchiveTransportFailure? result)
    {
        if (result is not { } failure)
        {
            return new ArchiveObjectWritten();
        }

        return failure switch
        {
            ArchiveObjectMissing missing => new ArchiveUnavailable(missing.Message, "archive_bucket_missing"),
            ArchiveUnavailable unavailable => unavailable,
            ArchiveTimedOut timeout => timeout,
        };
    }

    private async ValueTask<ArchiveTransportFailure?> InvokeAsync(
        Func<AmazonS3Client, string, CancellationToken, Task> call,
        CancellationToken cancellationToken)
    {
        if (_client is not { } client || _bucket is not { } bucket)
        {
            return Disabled;
        }

        try
        {
            await call(client, bucket, cancellationToken);
            return null;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound && exception.ErrorCode != NoSuchBucket)
        {
            return new ArchiveObjectMissing(exception.Message, "archive_object_not_found");
        }
        catch (AmazonS3Exception exception)
        {
            return new ArchiveUnavailable(string.Concat("archive request failed: ", exception.Message), "archive_unavailable");
        }
        catch (HttpRequestException exception)
        {
            return new ArchiveUnavailable(string.Concat("archive unreachable: ", exception.Message), "archive_unreachable");
        }
        catch (IOException exception)
        {
            return new ArchiveUnavailable(string.Concat("archive transfer failed: ", exception.Message), "archive_io");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new ArchiveTimedOut(string.Concat("archive request timed out: ", exception.Message), "archive_timeout");
        }
    }
}
