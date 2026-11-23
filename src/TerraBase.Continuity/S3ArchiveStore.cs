using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace TerraBase.Continuity;

public sealed class S3ArchiveStore : IDisposable
{
    private const string NoSuchBucket = "NoSuchBucket";

    private static readonly Unavailable Disabled = new("continuous backup is disabled on this node", "backup_disabled");

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

    public async ValueTask<Result<Done, Unavailable, RequestTimeout>> UploadFileAsync(string key, string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Narrow(await InvokeAsync(
            async (client, bucket, token) =>
            {
                using var transfer = new TransferUtility(client);
                await transfer.UploadAsync(new TransferUtilityUploadRequest { BucketName = bucket, Key = key, FilePath = path }, token).ConfigureAwait(false);
                return new Done();
            },
            cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<Result<Done, Unavailable, RequestTimeout>> UploadStreamAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        return Narrow(await InvokeAsync(
            async (client, bucket, token) =>
            {
                _ = await client.PutObjectAsync(
                    new PutObjectRequest
                    {
                        BucketName = bucket,
                        Key = key,
                        InputStream = content,
                        AutoCloseStream = false,
                        UseChunkEncoding = false,
                        ContentType = contentType,
                    },
                    token).ConfigureAwait(false);
                return new Done();
            },
            cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<Result<Done, Unavailable, RequestTimeout>> PutBytesAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        return await UploadStreamAsync(key, stream, contentType, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<Result<byte[], NotFound, Unavailable, RequestTimeout>> GetBytesAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return InvokeAsync(
            async (client, bucket, token) =>
            {
                using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                await response.ResponseStream.CopyToAsync(buffer, token).ConfigureAwait(false);
                return buffer.ToArray();
            },
            cancellationToken);
    }

    public ValueTask<Result<long, NotFound, Unavailable, RequestTimeout>> DownloadToFileAsync(string key, string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return InvokeAsync(
            async (client, bucket, token) =>
            {
                using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, token).ConfigureAwait(false);
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                await response.ResponseStream.CopyToAsync(file, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
                return file.Length;
            },
            cancellationToken);
    }

    public async ValueTask<Result<IReadOnlyList<string>, Unavailable, RequestTimeout>> ListKeysAsync(string prefix, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var listed = await InvokeAsync((client, bucket, token) => ListAllAsync(client, bucket, prefix, token), cancellationToken).ConfigureAwait(false);
        return listed switch
        {
            IReadOnlyList<string> keys => Listed(keys),
            NotFound notFound => new Unavailable(notFound.Message ?? "archive bucket not found", "archive_bucket_missing"),
            Unavailable unavailable => unavailable,
            RequestTimeout timeout => timeout,
        };

        static Result<IReadOnlyList<string>, Unavailable, RequestTimeout> Listed(IReadOnlyList<string> keys) => keys;
    }

    public async ValueTask<Result<Done, Unavailable, RequestTimeout>> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var deleted = await InvokeAsync(
            async (client, bucket, token) =>
            {
                _ = await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, token).ConfigureAwait(false);
                return new Done();
            },
            cancellationToken).ConfigureAwait(false);
        return deleted switch
        {
            Done done => done,
            NotFound => new Done(),
            Unavailable unavailable => unavailable,
            RequestTimeout timeout => timeout,
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
                cancellationToken).ConfigureAwait(false);
            if (response.S3Objects is { } objects)
            {
                keys.AddRange(objects.Select(static item => item.Key));
            }

            continuation = response.IsTruncated is true ? response.NextContinuationToken : null;
        }
        while (continuation is not null);

        return keys;
    }

    private static Result<Done, Unavailable, RequestTimeout> Narrow(Result<Done, NotFound, Unavailable, RequestTimeout> result) => result switch
    {
        Done done => done,
        NotFound notFound => new Unavailable(notFound.Message ?? "archive bucket not found", "archive_bucket_missing"),
        Unavailable unavailable => unavailable,
        RequestTimeout timeout => timeout,
    };

    private async ValueTask<Result<T, NotFound, Unavailable, RequestTimeout>> InvokeAsync<T>(
        Func<AmazonS3Client, string, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        if (_client is not { } client || _bucket is not { } bucket)
        {
            return Disabled;
        }

        try
        {
            return await call(client, bucket, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound && exception.ErrorCode != NoSuchBucket)
        {
            return new NotFound(exception.Message, "archive_object_not_found");
        }
        catch (AmazonS3Exception exception)
        {
            return new Unavailable(string.Concat("archive request failed: ", exception.Message), "archive_unavailable");
        }
        catch (HttpRequestException exception)
        {
            return new Unavailable(string.Concat("archive unreachable: ", exception.Message), "archive_unreachable");
        }
        catch (IOException exception)
        {
            return new Unavailable(string.Concat("archive transfer failed: ", exception.Message), "archive_io");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new RequestTimeout(string.Concat("archive request timed out: ", exception.Message), "archive_timeout");
        }
    }
}
