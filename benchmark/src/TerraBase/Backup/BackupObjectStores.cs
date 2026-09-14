using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace TerraBase.Backup;

/// <summary>Pluggable object storage contract used by backup and restore.</summary>
public interface IBackupObjectStore
{
    string Name { get; }

    /// <summary>Stores an immutable object, resuming from a provider checkpoint when supplied.</summary>
    Task<BackupObjectMetadata> PutAsync(
        string key,
        Stream content,
        string sha256,
        BackupUploadCheckpoint? checkpoint = null,
        CancellationToken cancellationToken = default);

    ValueTask<Stream> GetAsync(string key, CancellationToken cancellationToken = default);

    ValueTask<BackupObjectMetadata?> HeadAsync(string key, CancellationToken cancellationToken = default);

    IAsyncEnumerable<BackupObjectMetadata> ListAsync(
        string prefix,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>A deterministic object store for backup tests and local recovery drills.</summary>
public sealed class InMemoryBackupObjectStore : IBackupObjectStore
{
    private sealed class PartialUpload
    {
        public required string Key { get; init; }

        public required MemoryStream Content { get; init; }
    }

    private readonly object gate = new();
    private readonly Dictionary<string, (byte[] Content, BackupObjectMetadata Metadata)> objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PartialUpload> partialUploads = new(StringComparer.Ordinal);
    private int failuresRemaining;
    private long failAfterBytes = -1;

    /// <summary>
    /// Configures deterministic failures after the supplied byte count. The
    /// resulting exception carries a checkpoint and is intended to exercise
    /// manager retry/resume behavior.
    /// </summary>
    public void FailNextUploadAfter(long bytes, int count = 1)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        lock (gate)
        {
            failAfterBytes = bytes;
            failuresRemaining = count;
        }
    }

    public string Name => "memory";

    public Task<BackupObjectMetadata> PutAsync(
        string key,
        Stream content,
        string sha256,
        BackupUploadCheckpoint? checkpoint = null,
        CancellationToken cancellationToken = default)
    {
        BackupValidation.ValidateObjectKey(key);
        ArgumentNullException.ThrowIfNull(content);
        ValidateHash(sha256);
        if (!content.CanRead || (!content.CanSeek && checkpoint is not null))
            throw new ArgumentException("Resumable backup uploads require a readable seekable stream.", nameof(content));

        return PutCoreAsync(key, content, sha256, checkpoint, cancellationToken);
    }

    private async Task<BackupObjectMetadata> PutCoreAsync(
        string key,
        Stream content,
        string sha256,
        BackupUploadCheckpoint? checkpoint,
        CancellationToken cancellationToken)
    {
        var uploadId = checkpoint?.UploadId ?? Guid.NewGuid().ToString("N");
        PartialUpload partial;
        lock (gate)
        {
            if (checkpoint is null)
            {
                partial = new PartialUpload { Key = key, Content = new MemoryStream() };
                partialUploads[uploadId] = partial;
            }
            else if (!partialUploads.TryGetValue(uploadId, out partial!))
            {
                throw new BackupUploadException("The resumable upload checkpoint is unknown.", checkpoint);
            }
            else if (partial.Key != key || partial.Content.Length != checkpoint.Offset)
            {
                throw new BackupUploadException("The resumable upload checkpoint does not match the object.", checkpoint);
            }
        }

        if (content.CanSeek)
            content.Position = checkpoint?.Offset ?? 0;

        var buffer = new byte[128 * 1024];
        try
        {
            while (true)
            {
                var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                cancellationToken.ThrowIfCancellationRequested();
                await partial.Content.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                var shouldFail = false;
                lock (gate)
                {
                    if (failuresRemaining > 0 && failAfterBytes >= 0 && partial.Content.Length >= failAfterBytes)
                    {
                        failuresRemaining--;
                        shouldFail = true;
                    }
                }

                if (shouldFail)
                    throw new BackupUploadException(
                        "The in-memory object store injected an upload interruption.",
                        new BackupUploadCheckpoint(uploadId, partial.Content.Length));
            }

            var bytes = partial.Content.ToArray();
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(sha256)))
                throw new InvalidDataException("Backup object hash verification failed.");

            var metadata = new BackupObjectMetadata(key, bytes.LongLength, actualHash, DateTimeOffset.UtcNow);
            lock (gate)
            {
                objects[key] = (bytes, metadata);
                partialUploads.Remove(uploadId);
            }

            return metadata;
        }
        catch
        {
            // A resumable failure intentionally leaves the partial upload in
            // place. Other errors are safe to retry from the same checkpoint.
            throw;
        }
    }

    public ValueTask<Stream> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        BackupValidation.ValidateObjectKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!objects.TryGetValue(key, out var value))
                throw new FileNotFoundException("The backup object was not found.", key);
            return ValueTask.FromResult<Stream>(new MemoryStream(value.Content, writable: false));
        }
    }

    public ValueTask<BackupObjectMetadata?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        BackupValidation.ValidateObjectKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return ValueTask.FromResult(objects.TryGetValue(key, out var value) ? value.Metadata : null);
    }

    public async IAsyncEnumerable<BackupObjectMetadata> ListAsync(
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        BackupObjectMetadata[] result;
        lock (gate)
        {
            result = objects
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value.Metadata)
                .ToArray();
        }

        foreach (var item in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        BackupValidation.ValidateObjectKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            objects.Remove(key);
            foreach (var uploadId in partialUploads.Where(pair => pair.Value.Key == key).Select(pair => pair.Key).ToArray())
                partialUploads.Remove(uploadId);
        }
        return ValueTask.CompletedTask;
    }

    private static void ValidateHash(string sha256)
    {
        if (!BackupValidation.IsSha256(sha256))
            throw new ArgumentException("Sha256 must be 64 lowercase hexadecimal characters.", nameof(sha256));
    }
}

/// <summary>
/// S3-compatible object store using operator-issued presigned URLs. The URL
/// factory supplies the endpoint for each operation, so AWS, OCI, Tigris,
/// MinIO, and other S3-compatible services can share this adapter without a
/// provider SDK or credential material in TerraBase.
/// </summary>
public sealed class S3CompatibleBackupObjectStore : IBackupObjectStore
{
    private readonly Func<BackupObjectOperation, string, Uri> urlFactory;
    private readonly HttpClient client;
    private readonly int maxAttempts;

    public S3CompatibleBackupObjectStore(
        Func<BackupObjectOperation, string, Uri> urlFactory,
        HttpClient? client = null,
        int maxAttempts = 4)
    {
        ArgumentNullException.ThrowIfNull(urlFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        this.urlFactory = urlFactory;
        this.client = client ?? new HttpClient();
        this.maxAttempts = maxAttempts;
    }

    public string Name => "s3-compatible";

    public async Task<BackupObjectMetadata> PutAsync(
        string key,
        Stream content,
        string sha256,
        BackupUploadCheckpoint? checkpoint = null,
        CancellationToken cancellationToken = default)
    {
        BackupValidation.ValidateObjectKey(key);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanSeek)
            throw new ArgumentException("S3-compatible retries and resumable uploads require a seekable stream.", nameof(content));
        if (!BackupValidation.IsSha256(sha256))
            throw new ArgumentException("Sha256 must be 64 lowercase hexadecimal characters.", nameof(sha256));

        var uri = GetUri(BackupObjectOperation.Put, key);
        var offset = checkpoint?.Offset ?? 0;
        if (offset > content.Length)
            throw new ArgumentException("The upload checkpoint is beyond the stream length.", nameof(checkpoint));
        content.Position = offset;
        for (var attempt = 1; ; attempt++)
        {
            content.Position = offset;
            using var request = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = new StreamContent(new NonDisposingStream(content)),
            };
            request.Content.Headers.ContentLength = content.Length - offset;
            request.Headers.TryAddWithoutValidation("x-amz-checksum-sha256", sha256);
            request.Headers.TryAddWithoutValidation("x-content-sha256", sha256);
            if (offset > 0)
                request.Headers.TryAddWithoutValidation("Content-Range", $"bytes {offset}-{content.Length - 1}/*");
            if (checkpoint is not null)
                request.Headers.TryAddWithoutValidation("x-upload-id", checkpoint.UploadId);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return new BackupObjectMetadata(key, content.Length, sha256, DateTimeOffset.UtcNow);

            var statusCode = (int)response.StatusCode;
            var transient = statusCode is 408 or 425 or 429 or >= 500;
            if (attempt >= maxAttempts || !transient)
                throw new HttpRequestException($"S3-compatible backup upload failed with {statusCode} ({response.ReasonPhrase}).");
            await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<Stream> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        BackupValidation.ValidateObjectKey(key);
        using var response = await SendWithRetryAsync(HttpMethod.Get, BackupObjectOperation.Get, key, null, cancellationToken).ConfigureAwait(false);
        return new MemoryStream(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false), writable: false);
    }

    public async ValueTask<BackupObjectMetadata?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        BackupValidation.ValidateObjectKey(key);
        using var response = await SendWithRetryAsync(HttpMethod.Head, BackupObjectOperation.Get, key, null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        var length = response.Content.Headers.ContentLength ?? 0;
        var hash = response.Headers.TryGetValues("x-amz-checksum-sha256", out var values)
            ? values.SingleOrDefault()
            : null;
        return new BackupObjectMetadata(key, length, BackupValidation.IsSha256(hash) ? hash! : new string('0', 64), DateTimeOffset.UtcNow);
    }

    public async IAsyncEnumerable<BackupObjectMetadata> ListAsync(
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        using var response = await SendWithRetryAsync(HttpMethod.Get, BackupObjectOperation.List, prefix, null, cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        foreach (var item in document.Descendants().Where(static element => element.Name.LocalName == "Contents"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = item.Elements().FirstOrDefault(static element => element.Name.LocalName == "Key")?.Value;
            if (string.IsNullOrWhiteSpace(key))
                continue;
            var sizeText = item.Elements().FirstOrDefault(static element => element.Name.LocalName == "Size")?.Value;
            var size = long.TryParse(sizeText, out var parsed) ? parsed : 0;
            yield return new BackupObjectMetadata(key, size, new string('0', 64), DateTimeOffset.UtcNow);
        }
    }

    public async ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        BackupValidation.ValidateObjectKey(key);
        using var response = await SendWithRetryAsync(HttpMethod.Delete, BackupObjectOperation.Delete, key, null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
    }

    private Uri GetUri(BackupObjectOperation operation, string key)
    {
        var uri = urlFactory(operation, key);
        if (uri is null || uri.Scheme is not (Uri.UriSchemeHttp or Uri.UriSchemeHttps))
            throw new InvalidOperationException("S3-compatible object URLs must use HTTP or HTTPS.");
        return uri;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpMethod method,
        BackupObjectOperation operation,
        string key,
        HttpContent? content,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        var uri = GetUri(operation, key);
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri) { Content = content };
            var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                return response;
            var status = (int)response.StatusCode;
            var transient = status is 408 or 425 or 429 or >= 500;
            if (attempt >= maxAttempts || !transient)
                throw new HttpRequestException($"S3-compatible backup request failed with {status} ({response.ReasonPhrase}).");
            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }
}
