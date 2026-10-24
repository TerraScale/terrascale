using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using TerraBase.Features.Backup;

namespace TerraBase.Fast.Replication;

/// <summary>A bucket of whole objects in memory, with each key's puts and the regions that read it.</summary>
internal sealed class MemoryObjectStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _reads = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, int> Puts { get; } = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Readers(string key) => _reads.TryGetValue(key, out var regions) ? [.. regions.Keys] : [];

    // The bucket as the machine of `region` reaches it.
    public IObjectStore For(string region) => new Client(this, region);

    private sealed class Client(MemoryObjectStore bucket, string region) : IObjectStore
    {
        public async Task PutAsync(string key, Stream content, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            bucket.Objects[key] = buffer.ToArray();
            _ = bucket.Puts.AddOrUpdate(key, 1, (_, count) => count + 1);
        }

        public async Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken)
        {
            if (!bucket.Objects.TryGetValue(key, out var bytes))
            {
                return false;
            }

            _ = bucket._reads.GetOrAdd(key, _ => new(StringComparer.Ordinal)).AddOrUpdate(region, 1, (_, count) => count + 1);
            using var stream = new MemoryStream(bytes, writable: false);
            await read(stream, cancellationToken);
            return true;
        }

        public async IAsyncEnumerable<StoredObject> ListAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var (key, bytes) in bucket.Objects.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)
                && (startAfter is null || string.CompareOrdinal(entry.Key, startAfter) > 0)).OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new StoredObject(key, bytes.Length);
                await Task.Yield();
            }
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
        {
            _ = bucket.Objects.TryRemove(key, out _);
            return Task.FromResult(true);
        }
    }
}
