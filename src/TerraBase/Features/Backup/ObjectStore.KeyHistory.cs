using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using TerraBase.Configuration;

namespace TerraBase.Features.Backup;

internal sealed partial class S3ObjectStore
{
    internal async Task<(string Text, string ETag)?> ReadKeyHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SafeAsync(_s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket,
                Key = KeyringHistory.ObjectName,
            }, cancellationToken)).ConfigureAwait(false);
            var bytes = new byte[16385];
            var length = await response.ResponseStream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (length is 0 or > 16384 || string.IsNullOrEmpty(response.ETag))
            {
                throw new TerraBaseConfigurationException("shared master key history is unreadable");
            }
            return (Encoding.ASCII.GetString(bytes, 0, length), response.ETag);
        }
        catch (IOException)
        {
            throw new TerraBaseConfigurationException("shared master key history cannot be read");
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound && error.ErrorCode == "NoSuchKey")
        {
            return null;
        }
        catch (AmazonS3Exception error) when ((int)error.StatusCode is >= 400 and < 500)
        {
            throw new TerraBaseConfigurationException("shared master key history cannot be read");
        }
    }

    internal async Task<bool> AppendKeyHistoryAsync(string text, string? etag, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(Encoding.ASCII.GetBytes(text));
        try
        {
            await SafeAsync(_s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket,
                Key = KeyringHistory.ObjectName,
                InputStream = content,
                AutoCloseStream = false,
                UseChunkEncoding = false,
                IfMatch = etag,
                IfNoneMatch = etag is null ? "*" : null,
                Headers = { CacheControl = "no-store" },
            }, cancellationToken)).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception error) when (error.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return false;
        }
        catch (AmazonS3Exception error) when ((int)error.StatusCode is >= 400 and < 500)
        {
            throw new TerraBaseConfigurationException("shared master key history cannot be durably extended");
        }
    }
}
