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
        // Customer files use their own identity. Tigris is accepted only in development tests.
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
        // Replays can only contain the same bytes. Content-Length and checksum are signed headers.
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
        return ValueTask.FromResult(new FileSignedUrl(file.Id, Client.GetPreSignedURL(request), expires, []));
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
            // Verify bytes rather than trusting caller-supplied object metadata or an opaque multipart ETag.
            var hash = await SHA256.HashDataAsync(response.ResponseStream, cancellationToken).ConfigureAwait(false);
            return Convert.ToBase64String(hash) == file.Sha256;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    public async ValueTask DeleteAsync(string region, string key, CancellationToken cancellationToken) =>
        _ = await Client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket(region), Key = key }, cancellationToken).ConfigureAwait(false);

    public void Dispose() => _client?.Dispose();
}
