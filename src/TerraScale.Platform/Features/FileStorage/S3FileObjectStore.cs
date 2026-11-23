using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace TerraScale.Platform.Features.FileStorage;

public readonly union ProjectPrefixErasure(Done, Unavailable);

public sealed class S3FileObjectStore : IDisposable
{
    private const int ProjectPageSize = 100;
    private const int ParallelDeletes = 16;
    private static readonly Unavailable Unconfigured = new("file object storage is not configured", "files_unconfigured");
    private readonly AmazonS3Client? _client;
    private readonly Protocol _protocol;
    private readonly TimeProvider _timeProvider;

    public S3FileObjectStore(PlatformOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider;
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

    public Result<FileSignedUrl, Unavailable> Upload(string region, string key, FileObject file)
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
            Unavailable unavailable => unavailable,
        };
    }

    public Result<FileSignedUrl, Unavailable> Download(string region, string key, FileObject file)
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
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<bool, Unavailable>> VerifyAsync(string region, string key, FileObject file, CancellationToken cancellationToken)
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
            return new Unavailable("the object store did not answer in time", "files_timeout");
        }
    }

    public async ValueTask<Result<Done, Unavailable>> DeleteAsync(string region, string key, CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            return Unconfigured;
        }

        try
        {
            _ = await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket(region), Key = key }, cancellationToken);
            return new Done();
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
            return new Unavailable("the object store did not answer in time", "files_timeout");
        }
    }

    public async ValueTask<ProjectPrefixErasure> EraseProjectAsync(string region, Ulid organization, Ulid project, CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            return Unconfigured;
        }

        var prefix = ProjectPrefix(organization, project);
        bool truncated;
        do
        {
            var listed = await ListProjectObjectsAsync(client, region, prefix, cancellationToken);
            var swept = await (listed switch
            {
                ProjectObjectPage page => ErasePageAsync(region, prefix, page, cancellationToken),
                Unavailable unavailable => ValueTask.FromResult<(bool Truncated, Unavailable? Failure)>((false, unavailable)),
            });
            if (swept.Failure is { } failure)
            {
                return failure;
            }

            truncated = swept.Truncated;
        }
        while (truncated);

        return new Done();
    }

    private async ValueTask<(bool Truncated, Unavailable? Failure)> ErasePageAsync(
        string region, string prefix, ProjectObjectPage page, CancellationToken cancellationToken)
    {
        if (page.Keys.Any(key => !key.StartsWith(prefix, StringComparison.Ordinal)) || (page.Truncated && page.Keys.Count == 0))
        {
            return (false, new Unavailable("the object store returned an invalid project listing", "files_invalid_listing"));
        }

        Unavailable? failure = null;
        await Parallel.ForEachAsync(page.Keys, new ParallelOptions { MaxDegreeOfParallelism = ParallelDeletes, CancellationToken = cancellationToken },
            async (key, token) =>
            {
                var removed = await DeleteAsync(region, key, token);
                if (removed switch { Done => null, Unavailable unavailable => unavailable } is { } deleteFailure)
                {
                    Interlocked.CompareExchange(ref failure, deleteFailure, null);
                }
            });
        return (page.Truncated, failure);
    }

    private static async ValueTask<ListProjectObjectsResult> ListProjectObjectsAsync(
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
            return new Unavailable("the object store did not answer in time", "files_timeout");
        }
    }

    public void Dispose() => _client?.Dispose();

    private static Result<string, Unavailable> Presign(AmazonS3Client client, GetPreSignedUrlRequest request)
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

    private static Unavailable Failed(Exception error) => new($"file storage is unavailable; retry ({error.Message})", "files_unavailable");

    private readonly record struct ProjectObjectPage(IReadOnlyList<string> Keys, bool Truncated);

    private readonly union ListProjectObjectsResult(ProjectObjectPage, Unavailable);
}
