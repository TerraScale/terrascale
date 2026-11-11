using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TerraBase.Configuration;

namespace TerraBase.Features.FileStorage;

internal sealed class S3FileObjectStore : IFileObjectStore, IDisposable
{
    private readonly AmazonS3Client? _client;
    private readonly TimeProvider _time;

    public S3FileObjectStore(TerraBaseOptions options, TimeProvider time, IHostEnvironment environment)
    {
        _time = time;
        if (options.FileStorage is not { } settings)
        {
            return;
        }
        var r2 = settings.Endpoint.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase);
        if (!r2 && (!environment.IsDevelopment() || settings.Endpoint.Host != "fly.storage.tigris.dev"))
        {
            return;
        }
        _client = new AmazonS3Client(new BasicAWSCredentials(settings.AccessKeyId, settings.SecretKey), new AmazonS3Config
        {
            ServiceURL = settings.Endpoint.ToString(),
            AuthenticationRegion = "auto",
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    public bool Configured => _client is not null;
    private AmazonS3Client Client => _client ?? throw new IOException("file object storage credentials are unavailable");
    private static string Bucket(string region) => "terrascale-" + region + "-files";

    public ValueTask<FileSignedUrl> UploadAsync(string region, string key, FileObject file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket(region),
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = file.UploadExpiresAt.UtcDateTime,
            ContentType = file.ContentType,
        };
        request.Headers.ContentLength = file.Size;
        request.Headers["x-amz-checksum-sha256"] = file.Sha256;
        return ValueTask.FromResult(new FileSignedUrl(file.Id, Client.GetPreSignedURL(request), file.UploadExpiresAt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Content-Type"] = file.ContentType,
                ["Content-Length"] = file.Size.ToString(CultureInfo.InvariantCulture),
                ["x-amz-checksum-sha256"] = file.Sha256,
            }));
    }

    public ValueTask<FileSignedUrl> DownloadAsync(string region, string key, FileObject file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expires = _time.GetUtcNow().AddSeconds(60);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket(region),
            Key = key,
            Verb = HttpVerb.GET,
            Expires = expires.UtcDateTime,
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentDisposition = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(file.Name),
                ContentType = "application/octet-stream",
                CacheControl = "private, no-store",
            },
        };
        return ValueTask.FromResult(new FileSignedUrl(file.Id, Client.GetPreSignedURL(request), expires,
            new Dictionary<string, string>()));
    }

    public async ValueTask<bool> VerifyAsync(string region, string key, FileObject file, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetObjectAsync(new GetObjectRequest { BucketName = Bucket(region), Key = key }, cancellationToken).ConfigureAwait(false);
            if (response.ContentLength != file.Size)
            {
                return false;
            }
            var hash = await SHA256.HashDataAsync(response.ResponseStream, cancellationToken).ConfigureAwait(false);
            return Convert.ToBase64String(hash) == file.Sha256;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound) { return false; }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw Unanswered(error); }
    }

    public async ValueTask DeleteAsync(string region, string key, CancellationToken cancellationToken)
    {
        try
        {
            _ = await Client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket(region), Key = key }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw Unanswered(error); }
    }

    public async ValueTask<FileObjectListing> ListAsync(string region, string prefix, CancellationToken cancellationToken)
    {
        try
        {
            var response = await Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket(region), Prefix = prefix }, cancellationToken).ConfigureAwait(false);
            return new([.. (response.S3Objects ?? []).Select(stored => stored.Key)], response.IsTruncated == true);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw Unanswered(error); }
    }

    private static IOException Unanswered(OperationCanceledException error) => new("the object store did not answer in time", error);

    public void Dispose() => _client?.Dispose();
}
