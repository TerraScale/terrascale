using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using TerraBase.Configuration;
using TerraBase.Primitives;

namespace TerraBase.Features.Backup;

internal sealed class DataKeys : IDisposable
{
    private readonly IObjectStore _store;
    private readonly IMasterKey _master;
    private readonly TimeProvider _time;
    private readonly bool _freshKey;
    private readonly KeyCacheRevocations? _revocations;
    private long _generation;
    private readonly Dictionary<string, int> _pendingScopes = new(StringComparer.Ordinal);
    private bool _disposed;

    public DataKeys(IObjectStore store, IMasterKey master, TimeProvider time, BackupSettings settings,
        bool freshKey = false, SemaphoreSlim? reads = null, KeyCacheRevocations? revocations = null)
    {
        _store = store;
        _master = master;
        _time = time;
        _freshKey = freshKey;
        _readSlots = reads ?? new(MaxReads, MaxReads);
        _waitForReadSlot = reads is null;
        _rotation = TimeSpan.FromDays(settings.KeyRotationDays);
        _current = new(new MemoryCacheOptions { Clock = new CacheClock(_time) });
        _keys = new(new MemoryCacheOptions { Clock = new CacheClock(_time) });
        _revocations = revocations;
        if (revocations is not null)
        {
            revocations.Shredded += Forget;
        }
    }

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(1);
    private const int CacheCapacity = 65536;
    private const int MaxWrappedKeyBytes = 16 * 1024;
    internal const int MaxReads = 8;
    private readonly SemaphoreSlim _readSlots;
    private readonly bool _waitForReadSlot;
    private readonly TimeSpan _rotation;
    private readonly MemoryCache _current;
    private readonly MemoryCache _keys;
    private readonly Lock _cacheAdmission = new();
    private readonly HashSet<string> _refreshing = new(StringComparer.Ordinal);
    private readonly Channel<string> _refreshes = Channel.CreateBounded<string>(64);
    private readonly SemaphoreSlim[] _selectionGates = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];
    private readonly SemaphoreSlim[] _keyGates = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    internal ChannelReader<string> Refreshes => _refreshes.Reader;

    internal Task<(string Id, byte[] Key)> ForSealingAsync(string scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_current.TryGetValue(scope, out CurrentKey? current) && current is not null && Fresh(current.Id))
        {
            if (_time.GetUtcNow() - current.CheckedAt >= RefreshInterval)
            {
                lock (_cacheAdmission)
                {
                    if (_refreshing.Add(scope) && !_refreshes.Writer.TryWrite(scope))
                    {
                        _refreshing.Remove(scope);
                    }
                }
            }
            return Task.FromResult((current.Id, current.Key));
        }
        return CurrentAsync(scope, cancellationToken);
    }

    internal async Task RefreshAsync(string scope, CancellationToken cancellationToken)
    {
        try
        {
            _ = await CurrentAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_cacheAdmission)
            {
                _refreshing.Remove(scope);
            }
        }
    }

    public async Task<(string Id, byte[] Key)> CurrentAsync(string scope, CancellationToken cancellationToken)
    {
        if (Current(scope) is { } cached)
        {
            return (cached.Id, cached.Key);
        }

        var gate = Gate(_selectionGates, scope);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var admitted = false;
        try
        {
            var generation = BeginRead(scope);
            admitted = true;
            if (Current(scope) is { } selected)
            {
                return (selected.Id, selected.Key);
            }

            _current.TryGetValue(scope, out CurrentKey? previous);
            var id = previous?.Id;
            if (!_freshKey)
            {
                var after = id is null ? null : BackupLayout.Key(scope, id);
                var prefix = BackupLayout.Keys(scope);
                await foreach (var item in _store.ListAsync(prefix, after, cancellationToken).ConfigureAwait(false))
                {
                    if (item.Key.Length == 0)
                    {
                        continue;
                    }

                    var candidate = item.Key[prefix.Length..];
                    if (candidate == BackupLayout.ShreddedName)
                    {
                        throw new DatabaseShreddedException((await ShredRecords.ReadAsync(_store, scope, cancellationToken).ConfigureAwait(false))?.ShreddedAt);
                    }

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
                if (_freshKey && await ShredRecords.ReadAsync(_store, scope, cancellationToken).ConfigureAwait(false) is { } priorShred)
                {
                    throw new DatabaseShreddedException(priorShred.ShreddedAt);
                }

                var (generated, wrapped) = await _master.GenerateDataKeyAsync(cancellationToken).ConfigureAwait(false);
                key = generated;
                id = EntityIds.New(_time).ToString();
                try
                {
                    if (wrapped.Length is 0 or > MaxWrappedKeyBytes)
                    {
                        throw new InvalidDataException("wrapped backup key exceeds its byte limit");
                    }

                    using var content = new MemoryStream(Encoding.ASCII.GetBytes(wrapped));
                    await _store.PutAsync(BackupLayout.Key(scope, id), content, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw;
                }
            }

            if (await ShredRecords.ReadAsync(_store, scope, cancellationToken).ConfigureAwait(false) is { } shredded)
            {
                _ = await _store.DeleteAsync(BackupLayout.Key(scope, id), cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(key);
                throw new DatabaseShreddedException(shredded.ShreddedAt);
            }

            lock (_cacheAdmission)
            {
                CheckGeneration(generation);
                Cache(_keys, (scope, id), key);
                Cache(_current, scope, new CurrentKey(id, key, _time.GetUtcNow()));
            }
            return (id, key);
        }
        finally
        {
            if (admitted)
            {
                EndRead(scope);
            }
            gate.Release();
        }
    }

    internal async Task<bool> IsHeldAsync(string scope, string id, CancellationToken cancellationToken)
    {
        await _readSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ShredRecords.ReadAsync(_store, scope, cancellationToken).ConfigureAwait(false) is { } shredded)
            {
                throw new DatabaseShreddedException(shredded.ShreddedAt);
            }

            return await _store.ReadAsync(BackupLayout.Key(scope, id), static (_, _) => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readSlots.Release();
        }
    }

    public Task<byte[]> GetAsync(string scope, string id, CancellationToken cancellationToken) =>
        ReadKeyAsync(scope, id, cancellationToken);

    public Task<ShredRecord?> ShreddedAsync(string scope, CancellationToken cancellationToken) =>
        ShredRecords.ReadAsync(_store, scope, cancellationToken);

    private async Task<byte[]> ReadKeyAsync(string scope, string id, CancellationToken cancellationToken)
    {
        if (Cached(scope, id) is { } cached)
        {
            return cached;
        }

        var gate = Gate(_keyGates, BackupLayout.Key(scope, id));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var admitted = false;
        try
        {
            var generation = BeginRead(scope);
            admitted = true;
            if (Cached(scope, id) is { } selected)
            {
                return selected;
            }

            if (await ShredRecords.ReadAsync(_store, scope, cancellationToken).ConfigureAwait(false) is { } priorShred)
            {
                Forget(scope);
                throw new DatabaseShreddedException(priorShred.ShreddedAt);
            }

            if (_waitForReadSlot)
            {
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
                    if (!await _store.ReadAsync(BackupLayout.Key(scope, id), async (content, readToken) =>
                        {
                            var length = await content.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, readToken).ConfigureAwait(false);
                            if (length > MaxWrappedKeyBytes)
                            {
                                throw new InvalidDataException("backup wrapped data key exceeds its byte limit");
                            }
                            wrapped = Encoding.ASCII.GetString(buffer, 0, length);
                        }, cancellationToken).ConfigureAwait(false) || string.IsNullOrEmpty(wrapped))
                    {
                        if (await ShredRecords.ReadAsync(_store, scope, cancellationToken).ConfigureAwait(false) is { } shredded)
                        {
                            throw new DatabaseShreddedException(shredded.ShreddedAt);
                        }

                        throw new InvalidDataException("a backup object names a data key the bucket does not hold");
                    }

                    var key = await _master.UnwrapAsync(wrapped, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        lock (_cacheAdmission)
                        {
                            CheckGeneration(generation);
                            Cache(_keys, (scope, id), key);
                            return key;
                        }
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
            if (admitted)
            {
                EndRead(scope);
            }
            gate.Release();
        }
    }

    public void Forget(string scope)
    {
        lock (_cacheAdmission)
        {
            if (_disposed)
            {
                return;
            }
            var affected = _pendingScopes.ContainsKey(scope) || _current.TryGetValue(scope, out _);
            _current.Remove(scope);
            foreach (var id in _keys.Keys)
            {
                if (id is ValueTuple<string, string> key && key.Item1 == scope)
                {
                    _keys.Remove(id);
                    affected = true;
                }
            }
            if (affected)
            {
                Interlocked.Increment(ref _generation);
            }
        }
    }

    public void Dispose()
    {
        if (_revocations is not null)
        {
            _revocations.Shredded -= Forget;
        }
        lock (_cacheAdmission)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
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
        }
    }

    private long BeginRead(string scope)
    {
        lock (_cacheAdmission)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pendingScopes[scope] = _pendingScopes.GetValueOrDefault(scope) + 1;
            return _generation;
        }
    }

    private void EndRead(string scope)
    {
        lock (_cacheAdmission)
        {
            if (--_pendingScopes[scope] == 0)
            {
                _pendingScopes.Remove(scope);
            }
        }
    }

    private void CheckGeneration(long generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (generation != Volatile.Read(ref _generation))
        {
            throw new IOException("data key selection raced a shred; retry against custody authority");
        }
    }

    private static SemaphoreSlim Gate(SemaphoreSlim[] gates, string key) =>
        gates[(StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % gates.Length];

    private void Cache<T>(MemoryCache cache, object id, T value)
    {
        lock (_cacheAdmission)
        {
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
            && _time.GetUtcNow() - current.CheckedAt < RefreshInterval && Fresh(current.Id)
            ? current
            : null;

    private static bool TryParseKey(string id, out Ulid key) => Ulid.TryParse(id, out key) && key.ToString() == id;

    private bool NotFuture(Ulid key) => key.CompareTo(Ulid.NewUlid(_time.GetUtcNow(),
        [255, 255, 255, 255, 255, 255, 255, 255, 255, 255])) <= 0;

    private bool Fresh(string id) => TryParseKey(id, out var key) && NotFuture(key)
        && _time.GetUtcNow() - key.Time is var age && age >= TimeSpan.Zero && age < _rotation;

    private sealed record CurrentKey(string Id, byte[] Key, DateTimeOffset CheckedAt);

    private sealed class CacheClock(TimeProvider _time) : ISystemClock
    {
        public DateTimeOffset UtcNow => _time.GetUtcNow();
    }
}
