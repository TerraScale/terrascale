using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TerraBase.Configuration;

namespace TerraBase.Features.Backup;

/// <summary>An object in the bucket.</summary>
/// <param name="Key">Its key, or empty for an unusable listing entry that still consumes scan budgets.</param>
/// <param name="Size">Its stored bytes.</param>
internal readonly record struct StoredObject(string Key, long Size);

/// <summary>The backup bucket: whole objects addressed by key.</summary>
internal interface IObjectStore
{
    /// <summary>Uploads <paramref name="content"/> (seekable, read from its position) as <paramref name="key"/>, replacing any object there.</summary>
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
/// An S3-compatible bucket through the AWS SDK, configured for OCI Object Storage's compatibility API: path-style
/// addressing, its own signing region, and checksums only where S3 requires them (OCI rejects the newer defaults).
/// </summary>
internal sealed class S3ObjectStore : IObjectStore, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> every request goes through.</summary>
    public const string HttpClientName = "backup-s3";

    public const string CustomerHttpClientName = "backup-s3-customer";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(15);

    private readonly AmazonS3Client _s3;
    private readonly string _bucket;
    private readonly bool _customer;
    private readonly TimeProvider _time;

    public S3ObjectStore(BackupSettings settings, IHttpClientFactory http, TimeProvider time)
        : this(settings.Endpoint, settings.Region, settings.Bucket, settings.AccessKeyId, settings.SecretKey, http, HttpClientName, time) { }

    public S3ObjectStore(Uri endpoint, string region, string bucket, string accessKeyId, string secretKey, IHttpClientFactory http, string clientName, TimeProvider time)
    {
        _bucket = bucket;
        _customer = clientName == CustomerHttpClientName;
        _time = time;
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
    }

    public async Task PutAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        using var deadline = _customer ? new CancellationTokenSource(RequestTimeout, _time) : null;
        using var stop = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = stop?.Token ?? cancellationToken;
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
