using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Util;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using TerraBase.Configuration;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Backup;

internal readonly record struct StoredObject(string Key, long Size);

internal interface IObjectStore
{
    Task PutAsync(string key, Stream content, CancellationToken cancellationToken);

    Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken);

    IAsyncEnumerable<StoredObject> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);
}

internal sealed class ObjectStoreBucketMissingException(string bucket) : IOException($"the backup bucket does not exist: {bucket}")
{
    public string Bucket => bucket;
}

internal sealed partial class S3ObjectStore : IObjectStore, IDisposable
{
    public const string HttpClientName = "backup-s3";

    public const string CustomerHttpClientName = "backup-s3-customer";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(15);

    private const long PartSize = 128L << 20;
    private const int MaxParts = 10_000;
    private const int PartsInFlight = 4;
    private const int PartAttempts = 3;
    private static readonly TimeSpan AbortTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan StaleUpload = UploadDeadline(MaxParts) + TimeSpan.FromHours(1);
    private const string UploadMetadata = "terrabase-upload";

    private readonly AmazonS3Client _s3;
    private readonly ConcurrentDictionary<AmazonWebServiceRequest, Action<WebServiceResponseEventArgs>> _responseReaders = new(ReferenceEqualityComparer.Instance);
    private readonly string _bucket;
    private readonly bool _customer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Counter<long>? _bucketMissing;

    public S3ObjectStore(BackupSettings settings, IHttpClientFactory http, TimeProvider time, ILogger<S3ObjectStore> logger, IMeterFactory meters)
        : this(settings.Endpoint, settings.Region, settings.Bucket, settings.AccessKeyId, settings.SecretKey, http, HttpClientName, time, logger,
            meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.backup.unproven_uploads_deleted",
                description: "Snapshots whose multipart upload completed without proof, deleted from the bucket."),
            BucketMissingCounter(meters))
    {
    }

    public S3ObjectStore(Uri endpoint, string region, string bucket, string accessKeyId, string secretKey, IHttpClientFactory http, string clientName, TimeProvider time,
        ILogger? logger = null, Counter<long>? unprovenDeleted = null, Counter<long>? bucketMissing = null)
    {
        _bucket = bucket;
        _customer = clientName == CustomerHttpClientName;
        _time = time;
        _logger = logger ?? NullLogger.Instance;
        UnprovenDeleted = unprovenDeleted;
        _bucketMissing = bucketMissing;
        _s3 = new(new BasicAWSCredentials(accessKeyId, secretKey), new AmazonS3Config
        {
            ServiceURL = endpoint.ToString(),
            AuthenticationRegion = region,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = RequestTimeout,
            HttpClientFactory = new NamedClients(http, clientName),
        });
        _s3.AfterResponseEvent += (_, args) =>
        {
            if (args is WebServiceResponseEventArgs response && response.Request is { } request && _responseReaders.TryGetValue(request, out var read))
            {
                read(response);
            }
        };
    }

    internal Counter<long>? UnprovenDeleted { get; }

    internal static Counter<long> BucketMissingCounter(IMeterFactory meters) =>
        meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.object_store.bucket_missing",
            description: "Backup bucket operations answered NoSuchBucket; the configured bucket does not exist.");

    public async Task PutAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        var start = content.Position;
        var length = content.Length - start;
        var parts = Math.Max(1, (length + PartSize - 1) / PartSize);
        if (parts > MaxParts)
        {
            throw new IOException("backup object exceeds the multipart upload limit");
        }

        var file = parts > 1 ? content as FileStream : null;
        using var deadline = _customer || file is not null ? new CancellationTokenSource(UploadDeadline(parts), _time) : null;
        using var stop = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = stop?.Token ?? cancellationToken;
        if (file is not null)
        {
            await PutPartsAsync(key, file.SafeFileHandle, start, length, (int)parts, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SafeAsync(_s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            Headers = { CacheControl = "no-store" },
            UseChunkEncoding = false,
        }, cancellationToken)).ConfigureAwait(false);
    }

    private static TimeSpan UploadDeadline(long parts) => RequestTimeout + TimeSpan.FromMinutes(parts - 1);

    private CustomerDeadline CustomerStop(CancellationToken cancellationToken) => new(
        _customer ? new CancellationTokenSource(RequestTimeout, _time) : null, cancellationToken);

    private readonly struct CustomerDeadline(CancellationTokenSource? deadline, CancellationToken cancellationToken) : IDisposable
    {
        private readonly CancellationTokenSource? deadlineSource = deadline;
        private readonly CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline?.Token ?? cancellationToken);

        public CancellationToken Token => linked.Token;

        public void Dispose()
        {
            linked.Dispose();
            deadlineSource?.Dispose();
        }
    }

    private async Task PutPartsAsync(string key, SafeFileHandle file, long start, long length, int parts, CancellationToken cancellationToken)
    {
        var id = Ulid.NewUlid().ToString();
        var initiate = new InitiateMultipartUploadRequest { BucketName = _bucket, Key = key };
        initiate.Metadata.Add(UploadMetadata, id);
        var upload = (await SafeAsync(_s3.InitiateMultipartUploadAsync(initiate, cancellationToken)).ConfigureAwait(false)).UploadId;
        if (string.IsNullOrEmpty(upload))
        {
            throw new InvalidDataException("backup bucket returned no multipart upload id");
        }

        var completed = false;
        try
        {
            var etags = new PartETag[parts];
            await Parallel.ForEachAsync(Enumerable.Range(1, parts), new ParallelOptions { MaxDegreeOfParallelism = PartsInFlight, CancellationToken = cancellationToken },
                async (part, token) =>
                {
                    var offset = (part - 1) * PartSize;
                    var etag = await PutPartAsync(key, upload, part, file, start + offset, Math.Min(PartSize, length - offset), token).ConfigureAwait(false);
                    etags[part - 1] = new(part, etag);
                }).ConfigureAwait(false);
            await SafeAsync(_s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                UploadId = upload,
                PartETags = [.. etags],
            }, cancellationToken)).ConfigureAwait(false);
            completed = true;
            var stored = await SafeAsync(_s3.GetObjectMetadataAsync(_bucket, key, cancellationToken)).ConfigureAwait(false);
            if (stored.ContentLength != length
                || stored.Metadata[UploadMetadata] != id && !(MultipartETag(etags) is { } etag && string.Equals(stored.ETag?.Trim('"'), etag, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException("backup object does not hold this multipart upload");
            }
        }
        catch
        {
            if (completed)
            {
                await DeleteUnprovenAsync(key).ConfigureAwait(false);
            }

            await AbortAsync(key, upload).ConfigureAwait(false);
            throw;
        }
    }

    private static string? MultipartETag(PartETag[] parts)
    {
        var digests = new byte[parts.Length * 16];
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].ETag?.Trim('"') is not { Length: 32 } hex
                || Convert.FromHexString(hex, digests.AsSpan(index * 16, 16), out _, out _) != OperationStatus.Done)
            {
                return null;
            }
        }

        return $"{Convert.ToHexStringLower(CryptoUtilFactory.CryptoInstance.ComputeMD5Hash(digests))}-{parts.Length}";
    }

    private async Task DeleteUnprovenAsync(string key)
    {
        if (!key.AsSpan(0, key.LastIndexOf('/') + 1).EndsWith("/snapshots/", StringComparison.Ordinal) || BackupLayout.ParseSnapshot(key) is null)
        {
            _logger.ZLogWarning($"Backup object {key:@Key} completed but its upload was not proven; it is not a snapshot, so it stays");
            return;
        }

        using var deadline = new CancellationTokenSource(AbortTimeout, _time);
        try
        {
            await SafeAsync(_s3.DeleteObjectAsync(_bucket, key, deadline.Token)).ConfigureAwait(false);
            UnprovenDeleted?.Add(1);
            _logger.ZLogWarning($"Deleted snapshot {key:@Key}: its upload completed but the bucket did not prove it");
        }
        catch (Exception error)
        {
            _logger.ZLogWarning(error, $"Deleting the unproven snapshot {key:@Key} failed; it stays in the bucket");
        }
    }

    private async Task<string> PutPartAsync(string key, string upload, int part, SafeFileHandle file, long offset, long length, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var content = new FilePart(file, offset, length);
                var response = await SafeAsync(_s3.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    UploadId = upload,
                    PartNumber = part,
                    InputStream = content,
                    UseChunkEncoding = false,
                }, cancellationToken)).ConfigureAwait(false);
                return response.ETag;
            }
            catch (Exception error) when (attempt < PartAttempts && !cancellationToken.IsCancellationRequested)
            {
                _logger.ZLogWarning(error, $"Part {part} of backup object {key:@Key} failed; it is retried alone");
            }
        }
    }

    public async Task AbortStaleUploadsAsync(string prefix, CancellationToken cancellationToken)
    {
        var request = new ListMultipartUploadsRequest { BucketName = _bucket, Prefix = prefix };
        try
        {
            while (true)
            {
                var page = await SafeAsync(_s3.ListMultipartUploadsAsync(request, cancellationToken)).ConfigureAwait(false);
                foreach (var upload in page.MultipartUploads ?? [])
                {
                    if (upload.Key is { } key && key.StartsWith(prefix, StringComparison.Ordinal) && !string.IsNullOrEmpty(upload.UploadId)
                        && upload.Initiated is { } initiated && _time.GetUtcNow() - new DateTimeOffset(DateTime.SpecifyKind(initiated, DateTimeKind.Utc)) > StaleUpload)
                    {
                        _logger.ZLogInformation($"Aborting the stale multipart upload of backup object {key:@Key} initiated {initiated:O}");
                        await AbortAsync(key, upload.UploadId).ConfigureAwait(false);
                    }
                }

                if (page.IsTruncated != true || page.NextKeyMarker is null
                    || (page.NextKeyMarker, page.NextUploadIdMarker) == (request.KeyMarker, request.UploadIdMarker))
                {
                    return;
                }

                (request.KeyMarker, request.UploadIdMarker) = (page.NextKeyMarker, page.NextUploadIdMarker);
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning(error, $"Listing the stale multipart uploads under {prefix:@Prefix} failed; the next run retries");
        }
    }

    private async Task AbortAsync(string key, string upload)
    {
        using var deadline = new CancellationTokenSource(AbortTimeout, _time);
        try
        {
            await SafeAsync(_s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = key, UploadId = upload }, deadline.Token))
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.ZLogWarning(error, $"Aborting the multipart upload of backup object {key:@Key} failed; its parts stay in the bucket until it is stale");
        }
    }

    public async Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var deadline = CustomerStop(cancellationToken);
        cancellationToken = deadline.Token;
        GetObjectResponse response;
        try
        {
            var request = new GetObjectRequest { BucketName = _bucket, Key = key };
            if (!_customer && key.StartsWith("catalog-anchor/", StringComparison.Ordinal))
            {
                request = Tigris(request, "X-Tigris-Consistent", "true");
            }

            response = await SafeAsync(_s3.GetObjectAsync(request, cancellationToken)).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound && error.ErrorCode != "NoSuchBucket")
        {
            return false;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            _bucketMissing?.Add(1);
            throw new ObjectStoreBucketMissingException(_bucket);
        }

        using (response)
        {
            var body = _customer ? new CustomerBody(response.ResponseStream) : response.ResponseStream;
            await read(body, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    public async IAsyncEnumerable<StoredObject> ListAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var deadline = CustomerStop(cancellationToken);
        cancellationToken = deadline.Token;
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, StartAfter = startAfter, MaxKeys = _customer ? 250 : 1000 };
        if (!_customer && prefix.StartsWith("catalog-anchor/", StringComparison.Ordinal))
        {
            request = Tigris(request, "X-Tigris-Consistent", "true");
        }

        var previous = startAfter;
        while (true)
        {
            ListObjectsV2Response page;
            try
            {
                page = await SafeAsync(_s3.ListObjectsV2Async(request, cancellationToken)).ConfigureAwait(false);
            }
            catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound && error.ErrorCode == "NoSuchBucket")
            {
                _bucketMissing?.Add(1);
                throw new ObjectStoreBucketMissingException(_bucket);
            }

            foreach (var item in page.S3Objects ?? [])
            {
                if (item.Key is not { } key || !key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("backup object listing returned a key outside its prefix");
                }

                if (previous is not null && CompareKeys(key, previous) <= 0)
                {
                    throw new InvalidDataException("backup object listing returned an out-of-order key");
                }

                previous = key;
                if (key.Length == 0 || Encoding.UTF8.GetByteCount(key) > 1024 || key.Split('/').Any(part => part is "." or "..") || item.Size is < 0)
                {
                    yield return new(string.Empty, 0);
                    continue;
                }

                yield return new(key, item.Size ?? 0);
            }

            if (page.IsTruncated != true)
            {
                yield break;
            }

            if (page.S3Objects is not { Count: > 0 } || string.IsNullOrEmpty(page.NextContinuationToken)
                || page.NextContinuationToken == request.ContinuationToken)
            {
                throw new InvalidDataException("backup object listing did not advance");
            }

            request.ContinuationToken = page.NextContinuationToken;
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        using var deadline = CustomerStop(cancellationToken);
        cancellationToken = deadline.Token;
        try
        {
            await SafeAsync(_s3.DeleteObjectAsync(_bucket, key, cancellationToken)).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception error) when (error.ErrorCode == "RetentionRuleViolation")
        {
            return false;
        }
    }

    public void Dispose() => _s3.Dispose();

    private static int CompareKeys(string left, string right)
    {
        var leftRunes = left.EnumerateRunes();
        var rightRunes = right.EnumerateRunes();
        while (leftRunes.MoveNext())
        {
            if (!rightRunes.MoveNext())
            {
                return 1;
            }

            var comparison = leftRunes.Current.Value.CompareTo(rightRunes.Current.Value);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return rightRunes.MoveNext() ? -1 : 0;
    }

    public async Task<IReadOnlyList<(string Version, DateTimeOffset At)>> ListSnapshotsAsync(CancellationToken cancellationToken)
    {
        var response = await _s3.ListBucketsAsync(Tigris(new ListBucketsRequest(), "X-Tigris-Snapshot", _bucket), cancellationToken).ConfigureAwait(false);
        return [.. (response.Buckets ?? []).Select(bucket => (bucket.BucketName.Split(';')[0],
            new DateTimeOffset(DateTime.SpecifyKind(bucket.CreationDate ?? DateTime.MinValue, DateTimeKind.Utc))))];
    }

    public async Task<bool> SnapshotsEnabledAsync(CancellationToken cancellationToken)
    {
        var request = new HeadBucketRequest { BucketName = _bucket };
        string? enabled = null;
        _responseReaders[request] = response =>
            enabled = response.ResponseHeaders.FirstOrDefault(header => header.Key.Equals("X-Tigris-Enable-Snapshot", StringComparison.OrdinalIgnoreCase)).Value;
        try
        {
            await _s3.HeadBucketAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            _bucketMissing?.Add(1);
            throw new ObjectStoreBucketMissingException(_bucket);
        }
        finally
        {
            _responseReaders.TryRemove(request, out _);
        }

        return enabled == "true";
    }

    public Task TakeSnapshotAsync(CancellationToken cancellationToken) =>
        _s3.PutBucketAsync(Tigris(new PutBucketRequest { BucketName = _bucket }, "X-Tigris-Snapshot", "true"), cancellationToken);

    public async Task DeleteSnapshotAsync(string version, CancellationToken cancellationToken)
    {
        if (version.Length == 0 || !version.All(char.IsAsciiDigit))
        {
            BucketDeleteRefusal(_logger, null);
            throw new BucketDeleteForbiddenException("snapshot version is not a number");
        }

        await _s3.DeleteBucketAsync(Tigris(new DeleteBucketRequest { BucketName = _bucket }, "X-Tigris-Snapshot-Version", version), cancellationToken).ConfigureAwait(false);
    }

    private static readonly Action<ILogger, Exception?> BucketDeleteRefusal = LoggerMessage.Define(
        LogLevel.Error, new EventId(2, "BucketDeleteRefused"), "A bucket delete was refused: TerraBase never deletes its backup bucket");

    private static T Tigris<T>(T request, string header, string value) where T : AmazonWebServiceRequest
    {
        ((Amazon.Runtime.Internal.IAmazonWebServiceRequest)request).AddBeforeRequestHandler(
            (_, args) => ((WebServiceRequestEventArgs)args).Headers[header] = value);
        return request;
    }

    private async Task<T> SafeAsync<T>(Task<T> request)
    {
        try
        {
            return await request.ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (_customer)
        {
            if (error.ErrorCode == "NoSuchBucket")
            {
                throw new HttpRequestException("customer backup bucket is unavailable", null, error.StatusCode);
            }

            if (error.StatusCode == HttpStatusCode.NotFound || error.ErrorCode == "RetentionRuleViolation")
            {
                throw new AmazonS3Exception("customer backup object operation failed")
                {
                    StatusCode = error.StatusCode,
                    ErrorCode = error.ErrorCode == "RetentionRuleViolation" ? "RetentionRuleViolation" : "NoSuchKey",
                };
            }

            throw new HttpRequestException("customer backup object operation failed", null, error.StatusCode);
        }
        catch (Exception error) when (_customer && error is not OperationCanceledException)
        {
            throw new HttpRequestException("customer backup object operation failed");
        }
    }

    private sealed class CustomerBody(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                return inner.Read(buffer);
            }
            catch (Exception error) when (IsBodyTransportFailure(error))
            {
                throw new HttpRequestException("customer backup object body could not be read");
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (IsBodyTransportFailure(error))
            {
                throw new HttpRequestException("customer backup object body could not be read");
            }
        }

        private static bool IsBodyTransportFailure(Exception error) => error is IOException or HttpRequestException or AmazonClientException
            or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException;
    }

    private sealed class FilePart(SafeFileHandle file, long start, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = RandomAccess.Read(file, buffer[..Remaining(buffer.Length)], start + _position);
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await RandomAccess.ReadAsync(file, buffer[..Remaining(buffer.Length)], start + _position, cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }

        private int Remaining(int count) => (int)Math.Clamp(length - _position, 0, count);
    }

    private sealed class NamedClients(IHttpClientFactory http, string name) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            var client = http.CreateClient(name);
            client.Timeout = clientConfig.Timeout ?? client.Timeout;
            return client;
        }
    }
}

internal sealed class BucketDeleteForbiddenException(string message) : Exception(message);
