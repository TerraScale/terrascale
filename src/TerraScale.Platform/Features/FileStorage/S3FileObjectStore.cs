using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ZLogger;

namespace TerraScale.Platform.Features.FileStorage;

public sealed record FileObjectDeleted;

public sealed record ProjectPrefixErased(bool DeletedAny);

public readonly union SignFileUploadOutcome(FileSignedUrl, PlatformUnavailable);

public readonly union SignFileDownloadOutcome(FileSignedUrl, PlatformUnavailable);

public readonly union VerifyFileObjectOutcome(bool, PlatformUnavailable);

public readonly union DeleteFileObjectOutcome(FileObjectDeleted, PlatformUnavailable);

public readonly union ProjectPrefixErasure(ProjectPrefixErased, PlatformUnavailable);

public sealed class S3FileObjectStore : IDisposable
{
    private const int ProjectPageSize = 100;
    private const int ParallelDeletes = 16;
    private static readonly PlatformUnavailable Unconfigured = new("file object storage is not configured", "files_unconfigured");
    private static readonly PlatformUnavailable Unavailable = new("file storage is unavailable; retry", "files_unavailable");
    private readonly AmazonS3Client? _client;
    private readonly Protocol _protocol;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<S3FileObjectStore> _logger;

    public S3FileObjectStore(PlatformOptions options, TimeProvider timeProvider, ILogger<S3FileObjectStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider;
        _logger = logger;
        if (options.Files is not { } files)
        {
            return;
        }

        _protocol = files.Endpoint.Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;
        _client = new AmazonS3Client(new BasicAWSCredentials(files.AccessKeyId, files.SecretAccessKey), new AmazonS3Config
        {
            ServiceURL = files.Endpoint.AbsoluteUri,
            AuthenticationRegion = "auto",
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    public static string Bucket(string region) => "terrascale-" + region + "-files";

    private static string ProjectPrefix(Ulid organization, Ulid project) => $"app-files/{organization}/{project}/";

    public static string Key(Ulid organization, Ulid project, string bucket, string file) => $"{ProjectPrefix(organization, project)}{bucket}/{file}";

    public SignFileUploadOutcome Upload(string region, string key, FileObject file)
    {
        if (_client is not { } client)
        {
            return Unconfigured;
        }

        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket(region),
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = file.UploadExpiresAt.UtcDateTime,
            ContentType = file.ContentType,
            Protocol = _protocol,
        };
        request.Headers.ContentLength = file.Size;
        request.Headers["x-amz-checksum-sha256"] = file.Sha256;
        return Presign(client, request) switch
        {
            string url => new FileSignedUrl(file.Id, url, file.UploadExpiresAt, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Content-Type"] = file.ContentType,
                ["Content-Length"] = file.Size.ToString(CultureInfo.InvariantCulture),
                ["x-amz-checksum-sha256"] = file.Sha256,
            }),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public SignFileDownloadOutcome Download(string region, string key, FileObject file)
    {
        if (_client is not { } client)
        {
            return Unconfigured;
        }

        var expires = _timeProvider.GetUtcNow().AddSeconds(60);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket(region),
            Key = key,
            Verb = HttpVerb.GET,
            Expires = expires.UtcDateTime,
            Protocol = _protocol,
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentDisposition = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(file.Name),
                ContentType = "application/octet-stream",
                CacheControl = "private, no-store",
            },
        };
        return Presign(client, request) switch
        {
            string url => new FileSignedUrl(file.Id, url, expires, new Dictionary<string, string>(StringComparer.Ordinal)),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<VerifyFileObjectOutcome> VerifyAsync(string region, string key, FileObject file, CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            return Unconfigured;
        }

        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = Bucket(region), Key = key }, cancellationToken);
            if (response.ContentLength != file.Size)
            {
                return false;
            }

            var hash = await SHA256.HashDataAsync(response.ResponseStream, cancellationToken);
            return Convert.ToBase64String(hash) == file.Sha256;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (AmazonServiceException error)
        {
            return Failed(error);
        }
        catch (AmazonClientException error)
        {
            return Failed(error);
        }
        catch (HttpRequestException error)
        {
            return Failed(error);
        }
        catch (IOException error)
        {
            return Failed(error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PlatformUnavailable("the object store did not answer in time", "files_timeout");
        }
    }

    public async ValueTask<DeleteFileObjectOutcome> DeleteAsync(string region, string key, CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            return Unconfigured;
        }

        try
        {
            _ = await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket(region), Key = key }, cancellationToken);
            return new FileObjectDeleted();
        }
        catch (AmazonServiceException error)
        {
            return Failed(error);
        }
        catch (AmazonClientException error)
        {
            return Failed(error);
        }
        catch (HttpRequestException error)
        {
            return Failed(error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PlatformUnavailable("the object store did not answer in time", "files_timeout");
        }
    }

    public async ValueTask<ProjectPrefixErasure> EraseProjectAsync(string region, Ulid organization, Ulid project, CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            return Unconfigured;
        }

        var prefix = ProjectPrefix(organization, project);
        var deletedAny = false;
        bool truncated;
        do
        {
            var listed = await ListProjectObjectsAsync(client, region, prefix, cancellationToken);
            var swept = listed switch
            {
                ProjectObjectPage page => await ErasePageAsync(region, prefix, page, cancellationToken),
                PlatformUnavailable unavailable => (Truncated: false, DeletedAny: false, Failure: unavailable),
            };
            if (swept.Failure is { } failure)
            {
                return failure;
            }

            deletedAny |= swept.DeletedAny;
            truncated = swept.Truncated;
        }
        while (truncated);

        return new ProjectPrefixErased(deletedAny);
    }

    private async ValueTask<(bool Truncated, bool DeletedAny, PlatformUnavailable? Failure)> ErasePageAsync(
        string region, string prefix, ProjectObjectPage page, CancellationToken cancellationToken)
    {
        if (page.Keys.Any(key => !key.StartsWith(prefix, StringComparison.Ordinal)) || (page.Truncated && page.Keys.Count == 0))
        {
            return (false, false, new PlatformUnavailable("the object store returned an invalid project listing", "files_invalid_listing"));
        }

        PlatformUnavailable? failure = null;
        await Parallel.ForEachAsync(page.Keys, new ParallelOptions { MaxDegreeOfParallelism = ParallelDeletes, CancellationToken = cancellationToken },
            async (key, token) =>
            {
                var removed = await DeleteAsync(region, key, token);
                if (removed switch { FileObjectDeleted => null, PlatformUnavailable unavailable => unavailable } is { } deleteFailure)
                {
                    Interlocked.CompareExchange(ref failure, deleteFailure, null);
                }
            });
        return (page.Truncated, page.Keys.Count > 0, failure);
    }

    private async ValueTask<ListProjectObjectsOutcome> ListProjectObjectsAsync(
        AmazonS3Client client, string region, string prefix, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = Bucket(region),
                Prefix = prefix,
                MaxKeys = ProjectPageSize,
            }, cancellationToken);
            return new ProjectObjectPage(
                response.S3Objects is { } objects ? [.. objects.Select(static item => item.Key)] : [], response.IsTruncated == true);
        }
        catch (AmazonS3Exception error) when (error.ErrorCode == "NoSuchBucket")
        {
            return new ProjectObjectPage([], false);
        }
        catch (AmazonServiceException error)
        {
            return Failed(error);
        }
        catch (AmazonClientException error)
        {
            return Failed(error);
        }
        catch (HttpRequestException error)
        {
            return Failed(error);
        }
        catch (IOException error)
        {
            return Failed(error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PlatformUnavailable("the object store did not answer in time", "files_timeout");
        }
    }

    public void Dispose() => _client?.Dispose();

    private PresignFileObjectOutcome Presign(AmazonS3Client client, GetPreSignedUrlRequest request)
    {
        try
        {
            return client.GetPreSignedURL(request);
        }
        catch (AmazonClientException error)
        {
            return Failed(error);
        }
    }

    private PlatformUnavailable Failed(Exception error)
    {
        _logger.ZLogWarning(error, $"File object storage request failed");
        return Unavailable;
    }

    private readonly record struct ProjectObjectPage(IReadOnlyList<string> Keys, bool Truncated);

    private readonly union PresignFileObjectOutcome(string, PlatformUnavailable);

    private readonly union ListProjectObjectsOutcome(ProjectObjectPage, PlatformUnavailable);
}
