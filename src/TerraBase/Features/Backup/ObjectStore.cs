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

/// <summary>An object in the bucket.</summary>
/// <param name="Key">Its key, or empty for an unusable listing entry that still consumes scan budgets.</param>
/// <param name="Size">Its stored bytes.</param>
internal readonly record struct StoredObject(string Key, long Size);

/// <summary>The backup bucket: whole objects addressed by key.</summary>
internal interface IObjectStore
{
    /// <summary>
    /// Uploads <paramref name="content"/> (seekable, read from its position) as <paramref name="key"/>, replacing any object there.
    /// A file larger than one part uploads in parts and keeps its position.
    /// </summary>
    Task PutAsync(string key, Stream content, CancellationToken cancellationToken);

    /// <summary>Streams <paramref name="key"/> to <paramref name="read"/>; <see langword="false"/> when it does not exist.</summary>
    Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken);

    /// <summary>Lists the objects under <paramref name="prefix"/> in key order, after <paramref name="startAfter"/> when set.</summary>
    /// <remarks>Unusable entries have an empty key and zero size so raw-item scan budgets still count them.</remarks>
    IAsyncEnumerable<StoredObject> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes <paramref name="key"/>; deleting a missing object succeeds. <see langword="false"/> when the bucket's retention
    /// rule (an object lock) still protects it.
    /// </summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// An S3-compatible bucket through the AWS SDK, configured for S3-compatible stores such as Tigris: path-style
/// addressing, its own signing region, and checksums only where S3 requires them (not every store accepts the newer defaults).
/// </summary>
internal sealed class S3ObjectStore : IObjectStore, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> every request goes through.</summary>
    public const string HttpClientName = "backup-s3";

    public const string CustomerHttpClientName = "backup-s3-customer";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(15);

    // Larger files upload in parts, so a failed or timed-out part restarts alone and no request nears S3's 5 GiB PUT cap.
    // S3 allows 10,000 parts: 128 MiB parts cover objects up to 1.25 TiB.
    private const long PartSize = 128L << 20;
    private const int MaxParts = 10_000;
    // Parts stream from the file at their own offsets: no part is held in memory.
    private const int PartsInFlight = 4;
    // Each attempt is one request under RequestTimeout, including the SDK's own retries of transient errors.
    private const int PartAttempts = 3;
    private static readonly TimeSpan AbortTimeout = TimeSpan.FromSeconds(5);
    // No multipart upload outlives the deadline of the largest one (MaxParts parts, about seven days), so one initiated longer
    // ago than that plus an hour (clock skew with the bucket, the owner's own abort) has no live owner on any node.
    internal static readonly TimeSpan StaleUpload = UploadDeadline(MaxParts) + TimeSpan.FromHours(1);
    // User metadata naming the upload that created an object (x-amz-meta-terrabase-upload).
    private const string UploadMetadata = "terrabase-upload";

    private readonly AmazonS3Client _s3;
    // Requests that read their response headers, with the reader. The client reads its response event twice per response
    // (a null check, then the call), so a reader subscribed and removed per request broke a request completing at the same
    // time (NullReferenceException in ProcessResponseHandlers): one handler stays subscribed for the client's life instead.
    private readonly ConcurrentDictionary<AmazonWebServiceRequest, Action<WebServiceResponseEventArgs>> _responseReaders = new(ReferenceEqualityComparer.Instance);
    private readonly string _bucket;
    private readonly bool _customer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public S3ObjectStore(BackupSettings settings, IHttpClientFactory http, TimeProvider time, ILogger<S3ObjectStore> logger, IMeterFactory meters)
        : this(settings.Endpoint, settings.Region, settings.Bucket, settings.AccessKeyId, settings.SecretKey, http, HttpClientName, time, logger,
            meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.backup.unproven_uploads_deleted",
                description: "Snapshots whose multipart upload completed without proof, deleted from the bucket."))
    {
    }

    public S3ObjectStore(Uri endpoint, string region, string bucket, string accessKeyId, string secretKey, IHttpClientFactory http, string clientName, TimeProvider time,
        ILogger? logger = null, Counter<long>? unprovenDeleted = null)
    {
        _bucket = bucket;
        _customer = clientName == CustomerHttpClientName;
        _time = time;
        _logger = logger ?? NullLogger.Instance;
        UnprovenDeleted = unprovenDeleted;
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

    /// <summary>The platform bucket's count of deleted unproven snapshots, shared with the customer buckets' stores.</summary>
    internal Counter<long>? UnprovenDeleted { get; }

    public async Task PutAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        var start = content.Position;
        var length = content.Length - start;
        var parts = Math.Max(1, (length + PartSize - 1) / PartSize);
        if (parts > MaxParts)
        {
            throw new IOException("backup object exceeds the multipart upload limit");
        }

        // A customer upload and every multipart upload keep one deadline for the whole object.
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
            // A signed payload hash rather than aws-chunked encoding, which S3-compatible services do not all accept.
            UseChunkEncoding = false,
        }, cancellationToken)).ConfigureAwait(false);
    }

    // Fifteen minutes, plus a minute for each further part (a sustained ~2 MiB/s).
    private static TimeSpan UploadDeadline(long parts) => RequestTimeout + TimeSpan.FromMinutes(parts - 1);

    private async Task PutPartsAsync(string key, SafeFileHandle file, long start, long length, int parts, CancellationToken cancellationToken)
    {
        // The completed object carries this upload's own id in its metadata, so it cannot be mistaken for an older object at the key.
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
            // S3-compatible services may answer a failed completion with 200 and an error body, and an older object of the same
            // length may sit at the key. The stored object proves this upload by its whole length and either this upload's id in
            // its metadata or S3's multipart ETag of this upload's own parts (stores need not keep metadata). Missing or other
            // evidence fails it (and aborts the upload if it still exists).
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

    // S3's ETag of a multipart object: the MD5 of its parts' binary MD5s (their ETags), then "-" and the part count. Null when a
    // part's ETag is not an MD5, as on stores with opaque ETags. MD5 is S3's protocol here, not a security check.
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

    // An unproven completed object would stay as a whole, unrecorded copy. Only a snapshot key is this attempt's alone: it names
    // the pin time, a generation and a position, ContinuousBackup stores each snapshot key once (a retry pins a new snapshot
    // under a new key), and nothing else writes snapshot keys. So only there is the object deleted; another key, such as a log
    // segment's that a retry rewrites, keeps its object.
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
                    // A signed payload hash per part, as for single uploads.
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

    /// <summary>
    /// Aborts the multipart uploads under <paramref name="prefix"/> initiated more than <see cref="StaleUpload"/> ago: left
    /// behind when their own abort failed or their node stopped. Best effort: a failure is logged and the next run retries.
    /// </summary>
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

    // Best effort under its own deadline, also after cancellation. An upload left behind is never visible as an object; its
    // parts hold bucket storage until AbortStaleUploadsAsync finds it past StaleUpload.
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
        // The SDK's response-header timeout does not cover streamed bodies. Pass the operation deadline into every
        // body read, including key retrieval and decoding, so cancellation ends the underlying I/O before returning.
        using var deadline = _customer ? new CancellationTokenSource(RequestTimeout, _time) : null;
        using var stop = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = stop?.Token ?? cancellationToken;
        GetObjectResponse response;
        try
        {
            response = await SafeAsync(_s3.GetObjectAsync(_bucket, key, cancellationToken)).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
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
        using var deadline = _customer ? new CancellationTokenSource(RequestTimeout, _time) : null;
        using var stop = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = stop?.Token ?? cancellationToken;
        // Leave room for metadata after sixfold XML expansion of 1024-byte keys within the customer 2 MiB limit.
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, StartAfter = startAfter, MaxKeys = _customer ? 250 : 1000 };
        var previous = startAfter;
        while (true)
        {
            var page = await SafeAsync(_s3.ListObjectsV2Async(request, cancellationToken)).ConfigureAwait(false);
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
                // Keep unusable entries in scan accounting without exposing their keys to backup operations.
                // A foreign or nonascending response above still fails before any operation.
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
        using var deadline = _customer ? new CancellationTokenSource(RequestTimeout, _time) : null;
        using var stop = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = stop?.Token ?? cancellationToken;
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

    // S3 orders UTF-8 bytes; valid Unicode scalar values have the same order, without allocating encoded keys.
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

    /// <summary>The Tigris snapshots of the bucket (<c>X-Tigris-Snapshot</c> on ListBuckets), by version.</summary>
    public async Task<IReadOnlyList<(string Version, DateTimeOffset At)>> ListSnapshotsAsync(CancellationToken cancellationToken)
    {
        var response = await _s3.ListBucketsAsync(Tigris(new ListBucketsRequest(), "X-Tigris-Snapshot", _bucket), cancellationToken).ConfigureAwait(false);
        // Each entry is named "<version>; name=<name>".
        return [.. (response.Buckets ?? []).Select(bucket => (bucket.BucketName.Split(';')[0],
            new DateTimeOffset(DateTime.SpecifyKind(bucket.CreationDate ?? DateTime.MinValue, DateTimeKind.Utc))))];
    }

    /// <summary>Whether the bucket has Tigris snapshots enabled (HeadBucket's <c>X-Tigris-Enable-Snapshot</c>).</summary>
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
        finally
        {
            _responseReaders.TryRemove(request, out _);
        }

        return enabled == "true";
    }

    /// <summary>Takes a Tigris snapshot of the whole bucket (<c>X-Tigris-Snapshot: true</c> on CreateBucket).</summary>
    public Task TakeSnapshotAsync(CancellationToken cancellationToken) =>
        _s3.PutBucketAsync(Tigris(new PutBucketRequest { BucketName = _bucket }, "X-Tigris-Snapshot", "true"), cancellationToken);

    /// <summary>Deletes one Tigris snapshot (<c>X-Tigris-Snapshot-Version</c> on DeleteBucket).</summary>
    public Task DeleteSnapshotAsync(string version, CancellationToken cancellationToken) =>
        // Without the version header this is a bucket delete: refuse anything but a version number.
        version.Length > 0 && version.All(char.IsAsciiDigit)
            ? _s3.DeleteBucketAsync(Tigris(new DeleteBucketRequest { BucketName = _bucket }, "X-Tigris-Snapshot-Version", version), cancellationToken)
            : throw new InvalidDataException("snapshot version is not a number");

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
            // Untrusted S3 errors can echo signed request fields. Preserve only statuses needed for normal control flow.
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

    // Sanitize transport reads only: validation in the consumer must keep its own integrity errors.
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

    // One part of a file, read at its own offsets so parts upload concurrently without sharing the file's position.
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

    // Requests go through IHttpClientFactory, so tests replace the transport like every other outbound client.
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
