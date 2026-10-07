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
    Task<bool> ReadAsync(string key, Func<Stream, Task> read, CancellationToken cancellationToken);

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
internal sealed class S3ObjectStore(BackupSettings settings, IHttpClientFactory http) : IObjectStore, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> every request goes through.</summary>
    public const string HttpClientName = "backup-s3";

    private readonly AmazonS3Client _s3 = new(new BasicAWSCredentials(settings.AccessKeyId, settings.SecretKey), new AmazonS3Config
    {
        ServiceURL = settings.Endpoint.ToString(),
        AuthenticationRegion = settings.Region,
        ForcePathStyle = true,
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        Timeout = TimeSpan.FromMinutes(15),
        HttpClientFactory = new NamedClients(http),
    });

    public async Task PutAsync(string key, Stream content, CancellationToken cancellationToken) =>
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            // A signed payload hash rather than aws-chunked encoding, which S3-compatible services do not all accept.
            UseChunkEncoding = false,
        }, cancellationToken).ConfigureAwait(false);

    public async Task<bool> ReadAsync(string key, Func<Stream, Task> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        GetObjectResponse response;
        try
        {
            response = await _s3.GetObjectAsync(settings.Bucket, key, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        using (response)
        {
            await read(response.ResponseStream).ConfigureAwait(false);
            return true;
        }
    }

    public async IAsyncEnumerable<StoredObject> ListAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request { BucketName = settings.Bucket, Prefix = prefix, StartAfter = startAfter };
        while (true)
        {
            var page = await _s3.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.S3Objects ?? [])
            {
                yield return new(item.Key, item.Size ?? 0);
            }

            if (page.IsTruncated != true)
            {
                yield break;
            }

            request.ContinuationToken = page.NextContinuationToken;
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await _s3.DeleteObjectAsync(settings.Bucket, key, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception error) when (error.ErrorCode == "RetentionRuleViolation")
        {
            return false;
        }
    }

    public void Dispose() => _s3.Dispose();

    // Requests go through IHttpClientFactory, so tests replace the transport like every other outbound client.
    private sealed class NamedClients(IHttpClientFactory http) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            var client = http.CreateClient(HttpClientName);
            client.Timeout = clientConfig.Timeout ?? client.Timeout;
            return client;
        }
    }
}
