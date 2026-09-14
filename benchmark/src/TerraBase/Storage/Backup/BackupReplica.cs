using System.Security.Cryptography;

namespace TerraBase.Storage.Backup;

/// <summary>Destination contract for immutable backup artifacts.</summary>
public interface IBackupReplica
{
    string Name { get; }

    /// <summary>Stores an artifact idempotently and verifies its content hash.</summary>
    Task PutAsync(string key, Stream content, string sha256, CancellationToken cancellationToken = default);
}

/// <summary>Atomic filesystem replica used by development and local drills.</summary>
public sealed class FileBackupReplica : IBackupReplica
{
    private readonly string root;

    public FileBackupReplica(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
    }

    public string Name => "file";

    public async Task PutAsync(string key, Stream content, string sha256, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        if (sha256.Length != 64)
            throw new ArgumentException("sha256 must be 64 hexadecimal characters.", nameof(sha256));
        byte[] expected;
        try { expected = Convert.FromHexString(sha256); }
        catch (FormatException exception) { throw new ArgumentException("sha256 must be hexadecimal.", nameof(sha256), exception); }
        var relative = key.Replace('\\', '/').TrimStart('/');
        if (relative.Contains("../", StringComparison.Ordinal) || relative.Equals("..", StringComparison.Ordinal))
            throw new ArgumentException("Backup keys cannot escape the replica root.", nameof(key));
        var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Backup keys cannot escape the replica root.", nameof(key));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".partial";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), expected))
                    throw new InvalidDataException("Backup artifact hash verification failed.");
            }
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }
}

/// <summary>
/// HTTPS object-store replica for OCI, Tigris, or any S3-compatible service
/// using operator-issued pre-signed PUT URLs.
/// </summary>
public sealed class PresignedHttpBackupReplica : IBackupReplica
{
    private readonly HttpClient client;
    private readonly Func<string, Uri> urlFactory;
    private readonly int maxAttempts;

    public PresignedHttpBackupReplica(string name, Func<string, Uri> urlFactory, HttpClient? client = null, int maxAttempts = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(urlFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        Name = name;
        this.urlFactory = urlFactory;
        this.client = client ?? new HttpClient();
        this.maxAttempts = maxAttempts;
    }

    public string Name { get; }

    public async Task PutAsync(string key, Stream content, string sha256, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        if (sha256.Length != 64) throw new ArgumentException("sha256 must be 64 hexadecimal characters.", nameof(sha256));
        var uri = urlFactory(key);
        if (uri is null || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup replica URLs must use HTTPS.");
        if (!content.CanSeek) throw new ArgumentException("Backup upload streams must be seekable so failed PUTs can be retried.", nameof(content));
        for (var attempt = 1; ; attempt++)
        {
            content.Position = 0;
            using var request = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = new StreamContent(new NonDisposingStream(content))
            };
            request.Headers.TryAddWithoutValidation("x-content-sha256", sha256);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return;
            var statusCode = (int)response.StatusCode;
            var transient = statusCode is 408 or 425 or 429 or >= 500;
            if (attempt >= maxAttempts || !transient)
                throw new HttpRequestException($"Backup upload failed with {statusCode} ({response.ReasonPhrase}).");
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
