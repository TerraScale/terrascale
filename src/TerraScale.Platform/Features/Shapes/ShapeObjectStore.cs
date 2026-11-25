using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace TerraScale.Platform.Features.Shapes;

public sealed record ShapeObjectStored;

public sealed record ShapeObjectDeleted;

public readonly union PutShapeObjectOutcome(ShapeObjectStored, PlatformUnavailable);

public readonly union GetShapeObjectOutcome(byte[], PlatformNotFound, PlatformUnavailable);

public readonly union DeleteShapeObjectOutcome(ShapeObjectDeleted, PlatformUnavailable);

public sealed class ShapeObjectStore : IDisposable
{
    private static readonly PlatformUnavailable Unconfigured = new("shape object storage is not configured", "shapes_unconfigured");
    private readonly AmazonS3Client? _client;
    private readonly ShapeStorageOptions? _options;

    public ShapeObjectStore(PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Shapes is not { } shapes)
        {
            return;
        }

        _options = shapes;
        _client = new AmazonS3Client(new BasicAWSCredentials(shapes.AccessKeyId, shapes.SecretAccessKey), new AmazonS3Config
        {
            ServiceURL = shapes.Endpoint.AbsoluteUri,
            Timeout = TimeSpan.FromSeconds(30),
            MaxErrorRetry = 2,
            AuthenticationRegion = "auto",
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    public bool IsConfigured => _client is not null;

    public static string ObjectName(long offset, string digest, string sealer) => $"{offset:D20}-{digest[..16]}-{sealer}.tbs1";

    public static string Key(string handle, string objectName) => $"v1/shapes/{handle}/{objectName}";

    public string Url(string handle, string objectName) =>
        _options is { } options ? new Uri(options.CdnBase, Key(handle, objectName)).AbsoluteUri : Key(handle, objectName);

    public async ValueTask<PutShapeObjectOutcome> PutAsync(string key, byte[] body, string contentType, string cacheControl, CancellationToken cancellationToken)
    {
        if (_client is not { } client || _options is not { } options)
        {
            return Unconfigured;
        }

        using var stream = new MemoryStream(body, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false,
            UseChunkEncoding = false,
        };
        request.Headers.CacheControl = cacheControl;
        try
        {
            _ = await client.PutObjectAsync(request, cancellationToken);
            return new ShapeObjectStored();
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
            return new PlatformUnavailable("shape object storage did not answer in time", "shapes_timeout");
        }
    }

    public async ValueTask<GetShapeObjectOutcome> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (_client is not { } client || _options is not { } options)
        {
            return Unconfigured;
        }

        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = options.Bucket, Key = key }, cancellationToken);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return new PlatformNotFound("the shape chunk is no longer retained", "shape_chunk_missing");
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
            return new PlatformUnavailable("shape object storage did not answer in time", "shapes_timeout");
        }
    }

    public async ValueTask<DeleteShapeObjectOutcome> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        if (_client is not { } client || _options is not { } options)
        {
            return Unconfigured;
        }

        try
        {
            _ = await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = options.Bucket, Key = key }, cancellationToken);
            return new ShapeObjectDeleted();
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return new ShapeObjectDeleted();
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
            return new PlatformUnavailable("shape object storage did not answer in time", "shapes_timeout");
        }
    }

    public void Dispose() => _client?.Dispose();

    private static PlatformUnavailable Failed(Exception error) => new($"shape object storage is unavailable ({error.Message})", "shapes_unavailable");
}
