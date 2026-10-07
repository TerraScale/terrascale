using System.Net;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TerraBase.Configuration;

namespace TerraBase.Features.Backup;

/// <summary>An object in the bucket.</summary>
/// <param name="Key">Its key.</param>
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
            await read(response.ResponseStream, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    public async IAsyncEnumerable<StoredObject> ListAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var deadline = _customer ? new CancellationTokenSource(RequestTimeout, _time) : null;
        using var stop = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = stop?.Token ?? cancellationToken;
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, StartAfter = startAfter, MaxKeys = 1000 };
        var previous = startAfter;
        while (true)
        {
            var page = await SafeAsync(_s3.ListObjectsV2Async(request, cancellationToken)).ConfigureAwait(false);
            foreach (var item in page.S3Objects ?? [])
            {
                if (item.Key is not { Length: > 0 and <= 1024 } key || !key.StartsWith(prefix, StringComparison.Ordinal)
                    || key.Split('/').Any(part => part is "." or "..") || (previous is not null && string.CompareOrdinal(key, previous) <= 0)
                    || item.Size is < 0)
                {
                    throw new InvalidDataException("backup object listing returned an invalid key or size");
                }

                previous = key;
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

    private async Task<T> SafeAsync<T>(Task<T> request)
    {
        try
        {
            return await request.ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (_customer)
        {
            // Untrusted S3 errors can echo signed request fields. Preserve only statuses needed for normal control flow.
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
