using System.Collections.Concurrent;
using System.Text.Json;

namespace TerraBase.E2E.PitrLab;

/// <summary>
/// Copies every object under some prefixes byte-exact from a source into a local root (the <see cref="DiskBucket"/>
/// layout s3-serve uses) and writes a manifest of what the root now holds. Resumable: an object already in the root whose
/// listed size and ETag match the previous manifest, and whose file still hashes to it, is not fetched again.
/// </summary>
internal static class PitrDownload
{
    private const int Parallelism = 8;

    public static async Task<List<ManifestEntry>> RunAsync(IPitrSource source, IReadOnlyList<string> prefixes, string root, string manifest, CancellationToken cancellationToken)
    {
        var destination = DiskBucket.Open(root, create: true);
        var previous = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        if (File.Exists(manifest))
        {
            await using var file = File.OpenRead(manifest);
            foreach (var entry in await JsonSerializer.DeserializeAsync(file, PitrJson.Default.ListManifestEntry, cancellationToken) ?? [])
            {
                previous[entry.Key] = entry;
            }
        }

        var done = new ConcurrentDictionary<string, ManifestEntry>(StringComparer.Ordinal);
        var vanished = new ConcurrentBag<string>();
        using var checkpoint = new SemaphoreSlim(1, 1);
        foreach (var prefix in prefixes)
        {
            await Parallel.ForEachAsync(source.ListAsync(prefix, cancellationToken), new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                async (item, token) =>
                {
                    if (previous.TryGetValue(item.Key, out var known) && known.Size == item.Size && known.Etag == item.ETag && destination.Contains(item.Key)
                        && await PitrSources.FingerprintAsync(destination.PathOf(item.Key), token) == new ObjectFingerprint(known.Size, known.Sha256))
                    {
                        done[item.Key] = known;
                        return;
                    }

                    ObjectFingerprint? stored = null;
                    if (!await source.ReadAsync(item.Key, async (content, readToken) => stored = await destination.PutAsync(item.Key, content, null, readToken), token))
                    {
                        // Deleted between the listing and the read (retention on a live bucket): reported, not fatal.
                        vanished.Add(item.Key);
                        return;
                    }

                    if (stored!.Size != item.Size)
                    {
                        throw new InvalidDataException($"{item.Key} read {stored.Size} bytes but was listed with {item.Size}");
                    }

                    done[item.Key] = new(item.Key, stored.Size, stored.Sha256, item.ETag);
                    if (done.Count % 500 == 0 && await checkpoint.WaitAsync(0, token))
                    {
                        try
                        {
                            await WriteAsync(manifest, previous.Values.Where(entry => !done.ContainsKey(entry.Key)).Concat(done.Values), token);
                        }
                        finally
                        {
                            checkpoint.Release();
                        }
                    }
                });
        }

        await checkpoint.WaitAsync(cancellationToken);
        var entries = await WriteAsync(manifest, done.Values, cancellationToken);
        Console.WriteLine($"download: {entries.Count} objects, {entries.Sum(entry => entry.Size)} bytes, {vanished.Count} vanished during the copy");
        foreach (var key in vanished.Order(StringComparer.Ordinal))
        {
            Console.WriteLine($"download: vanished {key}");
        }

        return entries;
    }

    private static async Task<List<ManifestEntry>> WriteAsync(string manifest, IEnumerable<ManifestEntry> entries, CancellationToken cancellationToken)
    {
        List<ManifestEntry> sorted = [.. entries.OrderBy(entry => entry.Key, S3KeyOrder.Instance)];
        await PitrJson.WriteAtomicAsync(manifest, sorted, PitrJson.Default.ListManifestEntry, cancellationToken);
        return sorted;
    }
}
