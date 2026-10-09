using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using TerraBase.Configuration;
using TerraBase.Primitives;

namespace TerraBase.Features.Backup;

/// <summary>
/// Per-project data keys (envelope encryption): generated and wrapped by the master key, stored wrapped in the bucket,
/// kept in the clear only in memory. A project uses its newest unexpired key; objects name the key that sealed them.
/// </summary>
/// <remarks>
/// Two members rotating a project's key at once both succeed; each object names its own persisted key. Current keys
/// refresh at most once a minute and are cached for at most 65536 scopes. Another 65536 unwrapped keys are cached for
/// restores (at most 4 MiB of key material in both caches), including concurrent downloads using the same key.
/// Entries expire after a day without access; a cache miss retrieves the retained wrapped key.
/// </remarks>
internal sealed class DataKeys(IObjectStore store, IMasterKey master, TimeProvider time, BackupSettings settings, bool freshKey = false, SemaphoreSlim? reads = null) : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(1);
    private const int CacheCapacity = 65536;
    private const int MaxWrappedKeyBytes = 16 * 1024;
    internal const int MaxReads = 8;
    // Customer managers share a separate host-wide pool; slow customer keys cannot consume platform capacity.
    private readonly SemaphoreSlim _readSlots = reads ?? new(MaxReads, MaxReads);
    private readonly bool _waitForReadSlot = reads is null;
    private readonly TimeSpan _rotation = TimeSpan.FromDays(settings.KeyRotationDays);
    private readonly MemoryCache _current = new(new MemoryCacheOptions { Clock = new CacheClock(time) });
    private readonly MemoryCache _keys = new(new MemoryCacheOptions { Clock = new CacheClock(time) });
    private readonly Lock _cacheAdmission = new();
    // ponytail: 64 stripes per operation bound synchronization memory; colliding selections or key reads serialize.
    // Separate arrays keep a slow publication from blocking historical reads and prevent re-entering a selection stripe.
    private readonly SemaphoreSlim[] _selectionGates = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];
    private readonly SemaphoreSlim[] _keyGates = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    /// <summary>The data key new objects of <paramref name="scope"/> are sealed with.</summary>
    public async Task<(string Id, byte[] Key)> CurrentAsync(string scope, CancellationToken cancellationToken)
    {
        if (Current(scope) is { } cached)
        {
            return (cached.Id, cached.Key);
        }

        var gate = Gate(_selectionGates, scope);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another file of the same project may have selected a key while this call waited.
            if (Current(scope) is { } selected)
            {
                return (selected.Id, selected.Key);
            }

            _current.TryGetValue(scope, out CurrentKey? previous);
            var id = previous?.Id;
            // Customer shipping starts with its own durable key, including when it mirrors both buckets.
            if (!freshKey)
            {
                var after = id is null ? null : BackupLayout.Key(scope, id);
                var prefix = BackupLayout.Keys(scope);
                await foreach (var item in store.ListAsync(prefix, after, cancellationToken).ConfigureAwait(false))
                {
                    if (item.Key.Length == 0)
                    {
                        continue;
                    }

                    var candidate = item.Key[prefix.Length..];
                    if (TryParseKey(candidate, out var candidateKey) && NotFuture(candidateKey))
                    {
                        id = candidate;
                    }
                }
            }

            byte[] key;
            if (id is not null && Fresh(id))
            {
                key = await ReadKeyAsync(scope, id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var (generated, wrapped) = await master.GenerateDataKeyAsync(cancellationToken).ConfigureAwait(false);
                key = generated;
                id = EntityIds.New(time).ToString();
                using var content = new MemoryStream(Encoding.ASCII.GetBytes(wrapped));
                await store.PutAsync(BackupLayout.Key(scope, id), content, cancellationToken).ConfigureAwait(false);
            }

            // Never select a key until its wrapped copy is durable; old objects keep naming old keys.
            Cache(_keys, (scope, id), key);
            Cache(_current, scope, new CurrentKey(id, key, time.GetUtcNow()));
            return (id, key);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The data key <paramref name="id"/> of <paramref name="scope"/>.</summary>
    /// <exception cref="InvalidDataException">The bucket holds no such key.</exception>
    public Task<byte[]> GetAsync(string scope, string id, CancellationToken cancellationToken) =>
        ReadKeyAsync(scope, id, cancellationToken);

    private async Task<byte[]> ReadKeyAsync(string scope, string id, CancellationToken cancellationToken)
    {
        if (Cached(scope, id) is { } cached)
        {
            return cached;
        }

        var gate = Gate(_keyGates, BackupLayout.Key(scope, id));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Cached(scope, id) is { } selected)
            {
                return selected;
            }

            if (_waitForReadSlot)
            {
                // Holding a key stripe bounds platform slot waiters to 64 without reserving another read buffer.
                await _readSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!await _readSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("backup data key read limit reached");
            }

            try
            {
                var buffer = new byte[MaxWrappedKeyBytes + 1];
                try
                {
                    string? wrapped = null;
                    if (!await store.ReadAsync(BackupLayout.Key(scope, id), async (content, readToken) =>
                        {
                            var length = await content.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, readToken).ConfigureAwait(false);
                            if (length > MaxWrappedKeyBytes)
                            {
                                throw new InvalidDataException("backup wrapped data key exceeds its byte limit");
                            }
                            wrapped = Encoding.ASCII.GetString(buffer, 0, length);
                        }, cancellationToken).ConfigureAwait(false) || string.IsNullOrEmpty(wrapped))
                    {
                        throw new InvalidDataException("a backup object names a data key the bucket does not hold");
                    }

                    var key = await master.UnwrapAsync(wrapped, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        Cache(_keys, (scope, id), key);
                        return key;
                    }
                    catch
                    {
                        CryptographicOperations.ZeroMemory(key);
                        throw;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer);
                }
            }
            finally
            {
                _readSlots.Release();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var id in _current.Keys)
        {
            if (_current.TryGetValue(id, out CurrentKey? current) && current is not null)
            {
                CryptographicOperations.ZeroMemory(current.Key);
            }
        }

        foreach (var id in _keys.Keys)
        {
            if (_keys.TryGetValue(id, out byte[]? key) && key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        _current.Dispose();
        _keys.Dispose();
        // Async-only gates never expose a wait handle. Let GC reclaim them after holders and waiters finish;
        // disposing a gate here could mask an in-flight operation's outcome when its finally releases it.
    }

    private static SemaphoreSlim Gate(SemaphoreSlim[] gates, string key) =>
        gates[(StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % gates.Length];

    private void Cache<T>(MemoryCache cache, object id, T value)
    {
        lock (_cacheAdmission)
        {
            // Bound admission by count: MemoryCache's size accounting can lag concurrent expiration and reject Set.
            // Make room synchronously before publishing the gate's result so waiters reuse it.
            if (cache.Count >= CacheCapacity && !cache.TryGetValue(id, out _))
            {
                cache.Compact(0.05);
            }

            cache.Set(id, value, new MemoryCacheEntryOptions { SlidingExpiration = CacheLifetime });
        }
    }

    private byte[]? Cached(string scope, string id) =>
        _current.TryGetValue(scope, out CurrentKey? current) && current?.Id == id
            ? current.Key
            : _keys.TryGetValue((scope, id), out byte[]? key) ? key : null;

    private CurrentKey? Current(string scope) =>
        _current.TryGetValue(scope, out CurrentKey? current) && current is not null
            && time.GetUtcNow() - current.CheckedAt < RefreshInterval && Fresh(current.Id)
            ? current
            : null;

    // The pinned parser accepts some invalid ASCII; only canonical IDs are persisted key names.
    private static bool TryParseKey(string id, out Ulid key) => Ulid.TryParse(id, out key) && key.ToString() == id;

    // Compare raw ULIDs before converting Time: a valid 48-bit timestamp may exceed DateTimeOffset's range.
    // Maximum randomness includes every key created in the current millisecond.
    private bool NotFuture(Ulid key) => key.CompareTo(Ulid.NewUlid(time.GetUtcNow(),
        [255, 255, 255, 255, 255, 255, 255, 255, 255, 255])) <= 0;

    private bool Fresh(string id) => TryParseKey(id, out var key) && NotFuture(key)
        && time.GetUtcNow() - key.Time is var age && age >= TimeSpan.Zero && age < _rotation;

    private sealed record CurrentKey(string Id, byte[] Key, DateTimeOffset CheckedAt);

    private sealed class CacheClock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}
