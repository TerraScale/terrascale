using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.Import;

/// <summary>
/// Staged sources on this node's volume, <c>&lt;data&gt;/.import/&lt;database id&gt;/</c>, within one budget: at most 2 GiB declared
/// over all staged sources, and an upload starts only if twice its size plus 1 GiB stays free (docs/terrabase/import.md
/// section 8).
/// </summary>
internal sealed class ImportStaging
{
    public const long MaxSourceBytes = 1L << 30;
    public const long MaxStagedBytes = 2L << 30;
    public const long MaxOrganizationBytes = 1L << 30;
    public static readonly TimeSpan IdleUpload = TimeSpan.FromDays(1);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Organization, long Bytes)> _reserved = [with(StringComparer.Ordinal)];

    public ImportStaging(TerraBaseOptions options)
    {
        Root = Path.Combine(options.DataDirectory, ".import");
        System.IO.Directory.CreateDirectory(Root);
        foreach (var manifest in Manifests())
        {
            _reserved[manifest.DatabaseId] = (manifest.OrganizationId, manifest.Bytes);
        }
    }

    public string Root { get; }

    public string Source(string databaseId) => Path.Combine(Directory(databaseId), "source");

    /// <summary>Whether this node staged (part of) the destination's source.</summary>
    public bool Has(string databaseId)
    {
        lock (_lock)
        {
            return _reserved.ContainsKey(databaseId);
        }
    }

    /// <summary>When the staged source (or, before its first byte, its reservation) last changed; null without either.</summary>
    public DateTimeOffset? LastWrite(string databaseId) =>
        File.Exists(Source(databaseId)) ? File.GetLastWriteTimeUtc(Source(databaseId))
        : File.Exists(Path.Combine(Directory(databaseId), "manifest.json")) ? File.GetLastWriteTimeUtc(Path.Combine(Directory(databaseId), "manifest.json"))
        : null;

    /// <summary>Bytes staged so far.</summary>
    public long Received(string databaseId) => File.Exists(Source(databaseId)) ? new FileInfo(Source(databaseId)).Length : 0;

    /// <summary>Reserves room for a source and writes its manifest; false when the budget or the volume has no room.</summary>
    public bool Reserve(ImportManifest manifest)
    {
        lock (_lock)
        {
            if (_reserved.ContainsKey(manifest.DatabaseId))
            {
                return true;
            }

            // No organization holds more than half of the node's staging budget.
            var free = new DriveInfo(Root).AvailableFreeSpace;
            if (_reserved.Values.Sum(entry => entry.Bytes) + manifest.Bytes > MaxStagedBytes
                || _reserved.Values.Where(entry => entry.Organization == manifest.OrganizationId).Sum(entry => entry.Bytes) + manifest.Bytes > MaxOrganizationBytes
                || free < (2 * manifest.Bytes) + (1L << 30))
            {
                return false;
            }

            System.IO.Directory.CreateDirectory(Directory(manifest.DatabaseId));
            File.WriteAllBytes(Path.Combine(Directory(manifest.DatabaseId), "manifest.json"),
                JsonSerializer.SerializeToUtf8Bytes(manifest, TerraBaseJsonContext.Default.ImportManifest));
            _reserved[manifest.DatabaseId] = (manifest.OrganizationId, manifest.Bytes);
            return true;
        }
    }

    /// <summary>Appends one part; returns the bytes written, which are fewer than asked when the body ends early.</summary>
    public async ValueTask<long> AppendAsync(string databaseId, Stream body, long length, CancellationToken cancellationToken)
    {
        var file = new FileStream(Source(databaseId), FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous);
        await using (file.ConfigureAwait(false))
        {
            var buffer = new byte[64 * 1024];
            long written = 0;
            try
            {
                while (written < length)
                {
                    var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - written)), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                }
            }
            finally
            {
                // Whatever arrived stays: the client continues from receivedBytes.
                await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            return written;
        }
    }

    /// <summary>Whether the staged source is complete and hashes to <paramref name="sha256"/>.</summary>
    public async ValueTask<bool> VerifyAsync(string databaseId, long bytes, string sha256, CancellationToken cancellationToken)
    {
        if (Received(databaseId) != bytes)
        {
            return false;
        }

        var file = new FileStream(Source(databaseId), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (file.ConfigureAwait(false))
        {
            var hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash) == sha256;
        }
    }

    /// <summary>Discards the staged bytes but keeps the reservation, for an upload of the same file.</summary>
    public void Discard(string databaseId)
    {
        if (File.Exists(Source(databaseId)))
        {
            File.Delete(Source(databaseId));
        }
    }

    /// <summary>Deletes the staged source and its manifest and releases the reservation.</summary>
    public void Delete(string databaseId)
    {
        lock (_lock)
        {
            if (System.IO.Directory.Exists(Directory(databaseId)))
            {
                System.IO.Directory.Delete(Directory(databaseId), recursive: true);
            }

            _reserved.Remove(databaseId);
        }
    }

    /// <summary>Deletes staging directories without a readable manifest (a crash while reserving).</summary>
    public void RemoveOrphans()
    {
        lock (_lock)
        {
            var named = Manifests().Select(manifest => manifest.DatabaseId).ToHashSet(StringComparer.Ordinal);
            foreach (var directory in System.IO.Directory.EnumerateDirectories(Root).Where(directory => !named.Contains(Path.GetFileName(directory))))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>Every staged source's manifest.</summary>
    public List<ImportManifest> Manifests()
    {
        var manifests = new List<ImportManifest>();
        foreach (var directory in System.IO.Directory.EnumerateDirectories(Root))
        {
            var path = Path.Combine(directory, "manifest.json");
            if (!EntityIds.IsValid(Path.GetFileName(directory)) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllBytes(path), TerraBaseJsonContext.Default.ImportManifest) is { } manifest
                    && manifest.DatabaseId == Path.GetFileName(directory))
                {
                    manifests.Add(manifest);
                }
            }
            catch (JsonException)
            {
                // A torn manifest (a crash while writing it) names nothing; RemoveOrphans deletes its directory.
            }
        }

        return manifests;
    }

    private string Directory(string databaseId) => EntityIds.IsValid(databaseId)
        ? Path.Combine(Root, databaseId)
        : throw new ArgumentException("a database id is a ULID", nameof(databaseId));
}
