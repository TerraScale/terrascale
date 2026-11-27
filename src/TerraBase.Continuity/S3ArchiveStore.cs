using System.Buffers;
using System.Diagnostics;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace TerraBase.Continuity;

public sealed record ArchiveObjectWritten;

public sealed record ArchiveConditionalWritten(string? ETag);

public sealed record ArchivePreconditionFailed(string Message, string Code = "archive_precondition_failed");

public sealed record ArchiveVersionedBytes(byte[] Content, string? ETag);

public sealed record ArchiveObjectMissing(string Message, string Code);

public sealed record ArchiveObjectTooLarge(string Key, long MaximumBytes);

public sealed record ArchiveUnavailable(string Message, string Code);

public sealed record ArchiveTimedOut(string Message, string Code);

public readonly union ArchiveWriteOutcome(ArchiveObjectWritten, ArchiveUnavailable, ArchiveTimedOut);

public readonly union ArchiveConditionalWriteOutcome(ArchiveConditionalWritten, ArchivePreconditionFailed, ArchiveUnavailable, ArchiveTimedOut);

public readonly union ArchiveBytesOutcome(byte[], ArchiveObjectMissing, ArchiveUnavailable, ArchiveTimedOut);

public readonly union ArchiveVersionedBytesOutcome(ArchiveVersionedBytes, ArchiveObjectMissing, ArchiveUnavailable, ArchiveTimedOut);

public readonly union ArchiveDownloadOutcome(long, ArchiveObjectMissing, ArchiveObjectTooLarge, ArchiveUnavailable, ArchiveTimedOut);

public readonly union ArchiveListOutcome(IReadOnlyList<string>, ArchiveUnavailable, ArchiveTimedOut);

internal readonly union ArchiveTransportFailure(ArchiveObjectMissing, ArchivePreconditionFailed, ArchiveUnavailable, ArchiveTimedOut);

public sealed class S3ArchiveStore : IDisposable
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private const string NoSuchBucket = "NoSuchBucket";

    private static readonly ArchiveUnavailable Disabled = new("continuous backup is disabled on this node", "backup_disabled");

    private readonly AmazonS3Client? _client;
    private readonly string? _bucket;
    private readonly ILogger<S3ArchiveStore> _logger;

    public S3ArchiveStore(ContinuityOptions options, ILogger<S3ArchiveStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
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
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_SUPPORTED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_SUPPORTED,
            Timeout = RequestTimeout,
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
                using var deadline = new CancellationTokenSource(RequestTimeout);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                await using var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                _ = await client.PutObjectAsync(Put(bucket, key, content, ManifestCodec.BinaryContentType), request.Token);
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

    public async ValueTask<ArchiveConditionalWriteOutcome> PutBytesIfMatchAsync(
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        string? expectedETag,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        string? writtenETag = null;
        var uploaded = await InvokeAsync(
            async (client, bucket, token) =>
            {
                using var stream = new MemoryStream(content.ToArray(), writable: false);
                var request = Put(bucket, key, stream, contentType);
                if (expectedETag is not null)
                {
                    request.IfMatch = expectedETag;
                }
                else
                {
                    request.IfNoneMatch = "*";
                }

                var response = await client.PutObjectAsync(request, token);
                writtenETag = response.ETag;
            },
            cancellationToken);
        if (uploaded is not { } failure)
        {
            return new ArchiveConditionalWritten(writtenETag);
        }

        return failure switch
        {
            ArchivePreconditionFailed conflict => conflict,
            ArchiveObjectMissing missing => new ArchiveUnavailable(missing.Message, "archive_bucket_missing"),
            ArchiveUnavailable unavailable => unavailable,
            ArchiveTimedOut timeout => timeout,
        };
    }

    public async ValueTask<ArchiveVersionedBytesOutcome> GetBytesWithETagAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        byte[]? payload = null;
        string? etag = null;
        var fetched = await InvokeAsync(
            async (client, bucket, token) =>
            {
                using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, token);
                using var buffer = new MemoryStream();
                await response.ResponseStream.CopyToAsync(buffer, token);
                payload = buffer.ToArray();
                etag = response.ETag;
            },
            cancellationToken);
        if (fetched is { } failure)
        {
            return failure switch
            {
                ArchiveObjectMissing missing => missing,
                ArchivePreconditionFailed conflict => new ArchiveUnavailable(conflict.Message, conflict.Code),
                ArchiveUnavailable unavailable => unavailable,
                ArchiveTimedOut timeout => timeout,
            };
        }

        if (payload is not { } content)
        {
            throw new UnreachableException();
        }

        return new ArchiveVersionedBytes(content, etag);
    }

    public async ValueTask<ArchiveBytesOutcome> GetBytesAsync(string key, CancellationToken cancellationToken)
    {
        var fetched = await GetBytesWithETagAsync(key, cancellationToken);
        return fetched switch
        {
            ArchiveVersionedBytes versioned => versioned.Content,
            ArchiveObjectMissing missing => missing,
            ArchiveUnavailable unavailable => unavailable,
            ArchiveTimedOut timeout => timeout,
        };
    }

    public ValueTask<ArchiveDownloadOutcome> DownloadToFileAsync(string key, string path, CancellationToken cancellationToken) =>
        DownloadToFileAsync(key, path, maximumBytes: null, cancellationToken);

    internal async ValueTask<ArchiveDownloadOutcome> DownloadToFileAsync(string key, string path, long? maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        long? length = null;
        var tooLarge = false;
        var fetched = await InvokeAsync(
            async (client, bucket, token) =>
            {
                using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, token);
                if (maximumBytes is { } limit && response.ContentLength > limit)
                {
                    tooLarge = true;
                    return;
                }

                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                if (maximumBytes is { } maximum)
                {
                    tooLarge = !await CopyWithinLimitAsync(response.ResponseStream, file, maximum, token);
                    if (tooLarge)
                    {
                        return;
                    }
                }
                else
                {
                    await response.ResponseStream.CopyToAsync(file, token);
                }

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
                ArchivePreconditionFailed conflict => new ArchiveUnavailable(conflict.Message, conflict.Code),
                ArchiveUnavailable unavailable => unavailable,
                ArchiveTimedOut timeout => timeout,
            };
        }

        if (tooLarge && maximumBytes is { } rejectedLimit)
        {
            return new ArchiveObjectTooLarge(key, rejectedLimit);
        }

        if (length is not { } bytes)
        {
            throw new UnreachableException();
        }

        return bytes;
    }

    private static async Task<bool> CopyWithinLimitAsync(Stream source, Stream destination, long remaining, CancellationToken cancellationToken)
    {
        using var buffer = MemoryPool<byte>.Shared.Rent(1 << 16);
        while (true)
        {
            var read = await source.ReadAsync(buffer.Memory[..((int)Math.Min(remaining, buffer.Memory.Length - 1L) + 1)], cancellationToken);
            if (read == 0)
            {
                return true;
            }

            if (read > remaining)
            {
                return false;
            }

            await destination.WriteAsync(buffer.Memory[..read], cancellationToken);
            remaining -= read;
        }
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
                ArchivePreconditionFailed conflict => new ArchiveUnavailable(conflict.Message, conflict.Code),
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
            ArchivePreconditionFailed conflict => new ArchiveUnavailable(conflict.Message, conflict.Code),
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
            ArchivePreconditionFailed conflict => new ArchiveUnavailable(conflict.Message, conflict.Code),
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
            return new ArchiveObjectMissing("the archive object does not exist", "archive_object_not_found");
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return new ArchivePreconditionFailed("the archive object changed concurrently");
        }
        catch (AmazonS3Exception exception)
        {
            ContinuityLog.ArchiveRequestFailed(_logger, exception);
            return new ArchiveUnavailable("the backup archive refused the request; retry", "archive_unavailable");
        }
        catch (AmazonClientException exception)
        {
            ContinuityLog.ArchiveRequestFailed(_logger, exception);
            return new ArchiveUnavailable("the backup archive client failed; retry", "archive_client_error");
        }
        catch (HttpRequestException exception)
        {
            ContinuityLog.ArchiveRequestFailed(_logger, exception);
            return new ArchiveUnavailable("the backup archive is unreachable; retry", "archive_unreachable");
        }
        catch (IOException exception)
        {
            ContinuityLog.ArchiveRequestFailed(_logger, exception);
            return new ArchiveUnavailable("the backup archive transfer failed; retry", "archive_io");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            ContinuityLog.ArchiveRequestFailed(_logger, exception);
            return new ArchiveTimedOut("the backup archive request timed out; retry", "archive_timeout");
        }
    }
}
