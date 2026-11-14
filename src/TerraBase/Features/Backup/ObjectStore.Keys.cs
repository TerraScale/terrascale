using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

namespace TerraBase.Features.Backup;

internal sealed partial class S3ObjectStore
{
    internal async Task<string?> RewrapKeyAsync(string path, IKeyring keyring, CancellationToken cancellationToken)
    {
        GetObjectResponse response;
        try
        {
            response = await SafeAsync(_s3.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = path }, cancellationToken)).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound && error.ErrorCode != "NoSuchBucket")
        {
            return null;
        }

        using (response)
        {
            var bytes = new byte[16 * 1024 + 1];
            var length = await response.ResponseStream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (length is 0 or > 16 * 1024 || string.IsNullOrEmpty(response.ETag))
            {
                throw new InvalidDataException("custody wrapped key or entity tag is malformed");
            }
            var wrapped = Encoding.ASCII.GetString(bytes, 0, length);
            var key = keyring.Unwrap(wrapped);
            try
            {
                var id = keyring.KeyId(wrapped);
                if (!keyring.NeedsRewrap(wrapped))
                {
                    return id;
                }
                using var content = new MemoryStream(Encoding.ASCII.GetBytes(keyring.Rewrap(key, path)));
                try
                {
                    await SafeAsync(_s3.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = _bucket,
                        Key = path,
                        InputStream = content,
                        AutoCloseStream = false,
                        UseChunkEncoding = false,
                        IfMatch = response.ETag,
                        Headers = { CacheControl = "no-store" },
                    }, cancellationToken)).ConfigureAwait(false);
                    return keyring.ActiveId;
                }
                catch (AmazonS3Exception error) when (error.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound
                    && error.ErrorCode != "NoSuchBucket")
                {
                    return id;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }
}
