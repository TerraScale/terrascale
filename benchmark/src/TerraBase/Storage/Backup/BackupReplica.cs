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
        if (sha256.Length != 64 || !Convert.TryFromHexString(sha256, new byte[32], out var hashLength) || hashLength != 32)
            throw new ArgumentException("sha256 must be 64 hexadecimal characters.", nameof(sha256));
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
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(sha256)))
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
