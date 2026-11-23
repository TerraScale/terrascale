using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TerraBase.Hosting;
using S3HttpClientFactory = Amazon.Runtime.HttpClientFactory;

namespace TerraBase.Features.Backup;

internal sealed class BackupDestinationProbe(IHttpClientFactory clients, TimeProvider time, IHostApplicationLifetime lifetime)
{
    public const string ClientName = "customer-backup";

    private static readonly BackendUnavailable Inaccessible = new("customer backup bucket could not be accessed", "backup_destination_unavailable");
    private static readonly BackendUnavailable Busy = new("customer backup configuration request limit reached", "backup_destination_busy");
    private static readonly byte[] ProbeContents = "TerraBase backup access check"u8.ToArray();

    private readonly Lock _gate = new();
    private readonly HashSet<Ulid> _inFlight = [];

    public async Task<BackendUnavailable?> ProbeAsync(BackupDestinationConfig destination, Ulid organization, Ulid project, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_inFlight.Count >= 4 || !_inFlight.Add(organization))
            {
                return Busy;
            }
        }

        try
        {
            using var client = new AmazonS3Client(new BasicAWSCredentials(destination.AccessKeyId, destination.SecretKey), new AmazonS3Config
            {
                ServiceURL = destination.Endpoint.AbsoluteUri,
                AuthenticationRegion = destination.Region,
                ForcePathStyle = true,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
                MaxErrorRetry = 0,
                HttpClientFactory = new ProbeHttpClientFactory(clients),
            });

            var prefix = $"v1/{organization}/{project}/";
            var key = $"{prefix}.probe/{Ulid.NewUlid()}";
            var verified = false;
            var removed = false;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), time);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                verified = await S3Async(token => VerifyAsync(client, destination.Bucket, prefix, key, token), timeout.Token, cancellationToken);
            }
            finally
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping, deadline.Token);
                removed = await S3Async(async token =>
                {
                    await client.DeleteObjectAsync(destination.Bucket, key, token);
                    return true;
                }, cleanup.Token, lifetime.ApplicationStopping);
            }

            return verified && removed ? null : Inaccessible;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(organization);
            }
        }
    }

    private static async Task<bool> VerifyAsync(AmazonS3Client client, string bucket, string prefix, string key, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(ProbeContents, writable: false);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            UseChunkEncoding = false,
            ContentType = "application/octet-stream",
        }, cancellationToken);

        using var response = await client.GetObjectAsync(bucket, key, cancellationToken);
        var received = new byte[ProbeContents.Length + 1];
        var length = await response.ResponseStream.ReadAtLeastAsync(received, received.Length, throwOnEndOfStream: false, cancellationToken);
        if (length != ProbeContents.Length || !received.AsSpan(0, length).SequenceEqual(ProbeContents))
        {
            return false;
        }

        await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, MaxKeys = 1 }, cancellationToken);
        return true;
    }

    private static async Task<bool> S3Async(Func<CancellationToken, Task<bool>> call, CancellationToken deadlineToken, CancellationToken cancellationToken)
    {
        try
        {
            return await call(deadlineToken);
        }
        catch (AmazonServiceException)
        {
            return false;
        }
        catch (AmazonClientException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private sealed class ProbeHttpClientFactory(IHttpClientFactory clients) : S3HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => clients.CreateClient(ClientName);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }
}
