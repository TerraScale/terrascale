using System.Collections.Concurrent;
using System.Text;

namespace TerraBase.Features.Backup;

/// <summary>
/// Per-project data keys (envelope encryption): generated and wrapped by the master key, stored wrapped in the bucket,
/// kept in the clear only in memory. A project uses its newest key; objects name the key that sealed them.
/// </summary>
/// <remarks>
/// ponytail: unwrapped keys stay cached for the process lifetime (32 bytes per project key); evict when projects number in
/// the millions. Two members creating a project's first key at once both succeed; each object names its own.
/// </remarks>
internal sealed class DataKeys(IObjectStore store, IMasterKey master, TimeProvider time) : IDisposable
{
    private readonly ConcurrentDictionary<(string Scope, string Id), byte[]> _keys = new();
    private readonly ConcurrentDictionary<string, string> _current = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _creating = new(1, 1);

    /// <summary>The data key new objects of <paramref name="scope"/> are sealed with.</summary>
    public async Task<(string Id, byte[] Key)> CurrentAsync(string scope, CancellationToken cancellationToken)
    {
        if (_current.TryGetValue(scope, out var id) && _keys.TryGetValue((scope, id), out var cached))
        {
            return (id, cached);
        }

        await _creating.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? newest = null;
            await foreach (var item in store.ListAsync(BackupLayout.Keys(scope), null, cancellationToken).ConfigureAwait(false))
            {
                newest = item.Key;
            }

            if (newest is not null)
            {
                id = newest[(newest.LastIndexOf('/') + 1)..];
                cached = await GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var (key, wrapped) = await master.GenerateDataKeyAsync(cancellationToken).ConfigureAwait(false);
                id = Ulid.NewUlid(time.GetUtcNow()).ToString();
                using var content = new MemoryStream(Encoding.ASCII.GetBytes(wrapped));
                await store.PutAsync(BackupLayout.Key(scope, id), content, cancellationToken).ConfigureAwait(false);
                cached = _keys.GetOrAdd((scope, id), key);
            }

            _current[scope] = id;
            return (id, cached);
        }
        finally
        {
            _creating.Release();
        }
    }

    /// <summary>The data key <paramref name="id"/> of <paramref name="scope"/>.</summary>
    /// <exception cref="InvalidDataException">The bucket holds no such key.</exception>
    public async Task<byte[]> GetAsync(string scope, string id, CancellationToken cancellationToken)
    {
        if (_keys.TryGetValue((scope, id), out var cached))
        {
            return cached;
        }

        string? wrapped = null;
        if (!await store.ReadAsync(BackupLayout.Key(scope, id), async content =>
            {
                using var reader = new StreamReader(content, Encoding.ASCII);
                wrapped = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false) || string.IsNullOrEmpty(wrapped))
        {
            throw new InvalidDataException("a backup object names a data key the bucket does not hold");
        }

        return _keys.GetOrAdd((scope, id), await master.UnwrapAsync(wrapped, cancellationToken).ConfigureAwait(false));
    }

    public void Dispose() => _creating.Dispose();
}
