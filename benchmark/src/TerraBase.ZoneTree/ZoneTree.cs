namespace TerraBase.ZoneTree;

/// <summary>Ordered storage operations used by TerraBase indexes.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface IZoneTree<TKey, TValue> : IDisposable
    where TKey : notnull
{
    /// <summary>The comparer defining key order.</summary>
    IComparer<TKey> Comparer { get; }

    /// <summary>The number of live keys.</summary>
    int Count { get; }

    /// <summary>Returns whether a key exists.</summary>
    bool ContainsKey(TKey key);

    /// <summary>Reads one key.</summary>
    bool TryGetValue(TKey key, out TValue value);

    /// <summary>Adds a key if absent and returns its operation index.</summary>
    bool TryAdd(TKey key, TValue value, out long operationIndex);

    /// <summary>Adds or replaces a key and returns its operation index.</summary>
    long Upsert(TKey key, TValue value);

    /// <summary>Removes a key if present and returns its operation index.</summary>
    bool TryRemove(TKey key, out long operationIndex);

    /// <summary>Returns an immutable point-in-time range result.</summary>
    IReadOnlyList<ZoneTreeEntry<TKey, TValue>> Scan(KeyRange<TKey> range);
}

/// <summary>
/// A comparer ordered, thread-safe in-memory ZoneTree core.
/// </summary>
/// <remarks>
/// Writes receive a monotonically increasing operation index. The index is
/// process-local until a durable WAL implementation is introduced; callers
/// must not treat it as a globally replicated revision. Range scans are
/// snapshots and therefore never hold the tree lock while the caller reads
/// the result.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class ZoneTree<TKey, TValue> : IZoneTree<TKey, TValue>
    where TKey : notnull
{
    private readonly RangeIndex<TKey, VersionedValue> index;
    private readonly IComparer<TKey> comparer;
    private readonly object gate = new();
    private long operationIndex;
    private bool disposed;

    /// <summary>Creates an empty in-memory tree.</summary>
    /// <param name="comparer">The comparer defining key order.</param>
    public ZoneTree(IComparer<TKey>? comparer = null)
    {
        comparer = comparer ?? Comparer<TKey>.Default;
        this.comparer = comparer;
        index = new RangeIndex<TKey, VersionedValue>(comparer);
    }

    /// <inheritdoc />
    public IComparer<TKey> Comparer => comparer;

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return index.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool ContainsKey(TKey key)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return index.ContainsKey(key);
        }
    }

    /// <inheritdoc />
    public bool TryGetValue(TKey key, out TValue value)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (index.TryGetValue(key, out var entry))
            {
                value = entry.Value;
                return true;
            }

            value = default!;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryAdd(TKey key, TValue value, out long operationIndex)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (index.ContainsKey(key))
            {
                operationIndex = 0;
                return false;
            }

            operationIndex = NextOperationIndex();
            index.Upsert(key, new VersionedValue(value, operationIndex));
            return true;
        }
    }

    /// <inheritdoc />
    public long Upsert(TKey key, TValue value)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var indexValue = new VersionedValue(value, NextOperationIndex());
            index.Upsert(key, indexValue);
            return indexValue.OperationIndex;
        }
    }

    /// <inheritdoc />
    public bool TryRemove(TKey key, out long operationIndex)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!index.TryRemove(key, out _))
            {
                operationIndex = 0;
                return false;
            }

            operationIndex = NextOperationIndex();
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ZoneTreeEntry<TKey, TValue>> Scan(KeyRange<TKey> range)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return index.Scan(range)
                .Select(static pair => new ZoneTreeEntry<TKey, TValue>(
                    pair.Key,
                    pair.Value.Value,
                    pair.Value.OperationIndex))
                .ToArray();
        }
    }

    /// <summary>Returns all live entries in comparer order.</summary>
    public IReadOnlyList<ZoneTreeEntry<TKey, TValue>> Scan() => Scan(KeyRange<TKey>.All);

    /// <summary>Returns a new factory for this tree's key/value types.</summary>
    public static ZoneTreeFactory<TKey, TValue> CreateFactory() => new();

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            index.Dispose();
            disposed = true;
        }
    }

    private long NextOperationIndex()
    {
        if (operationIndex == long.MaxValue)
        {
            throw new InvalidOperationException("The tree operation index is exhausted.");
        }

        return ++operationIndex;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private readonly record struct VersionedValue(TValue Value, long OperationIndex);
}

/// <summary>Configures and opens an in-memory ZoneTree.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class ZoneTreeFactory<TKey, TValue>
    where TKey : notnull
{
    private IComparer<TKey>? comparer;

    /// <summary>Sets the comparer used by opened trees.</summary>
    public ZoneTreeFactory<TKey, TValue> SetComparer(IComparer<TKey> comparer)
    {
        this.comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
        return this;
    }

    /// <summary>Opens an empty in-memory tree.</summary>
    public ZoneTree<TKey, TValue> OpenOrCreate() => new(comparer);
}
