namespace TerraBase.ZoneTree;

/// <summary>
/// A thread-safe comparer ordered index with snapshot range scans.
/// </summary>
/// <remarks>
/// The index is the in-memory ordered component of the storage layer. A scan
/// copies matching entries while holding the read lock and releases the lock
/// before returning, so a consumer cannot block writers by retaining the
/// returned collection. Durable segments can implement the same operations
/// behind the <see cref="IZoneTree{TKey,TValue}"/> contract.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class RangeIndex<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private readonly SortedDictionary<TKey, TValue> values;
    private readonly IComparer<TKey> comparer;
    private readonly object gate = new();
    private bool disposed;

    /// <summary>Creates an empty ordered index.</summary>
    /// <param name="comparer">The comparer defining key order.</param>
    public RangeIndex(IComparer<TKey>? comparer = null)
    {
        this.comparer = comparer ?? Comparer<TKey>.Default;
        values = new SortedDictionary<TKey, TValue>(this.comparer);
    }

    /// <summary>The comparer used by this index.</summary>
    public IComparer<TKey> Comparer => comparer;

    /// <summary>The number of keys in the index.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return values.Count;
            }
        }
    }

    /// <summary>Returns whether <paramref name="key"/> is indexed.</summary>
    public bool ContainsKey(TKey key)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return values.ContainsKey(key);
        }
    }

    /// <summary>Reads one value without exposing mutable internal state.</summary>
    public bool TryGetValue(TKey key, out TValue value)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return values.TryGetValue(key, out value!);
        }
    }

    /// <summary>Adds a key and fails if it already exists.</summary>
    public bool TryAdd(TKey key, TValue value)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (values.ContainsKey(key))
            {
                return false;
            }

            values.Add(key, value);
            return true;
        }
    }

    /// <summary>Adds or replaces a value.</summary>
    public void Upsert(TKey key, TValue value)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            values[key] = value;
        }
    }

    /// <summary>Removes a key if it exists.</summary>
    public bool TryRemove(TKey key, out TValue value)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!values.TryGetValue(key, out value!))
            {
                value = default!;
                return false;
            }

            values.Remove(key);
            return true;
        }
    }

    /// <summary>Returns a stable snapshot of a range in comparer order.</summary>
    public IReadOnlyList<KeyValuePair<TKey, TValue>> Scan(KeyRange<TKey> range)
    {
        range.Validate(comparer);
        lock (gate)
        {
            ThrowIfDisposed();
            var result = new List<KeyValuePair<TKey, TValue>>();
            foreach (var pair in values)
            {
                if (range.Includes(pair.Key, comparer))
                {
                    result.Add(pair);
                }
            }

            return result;
        }
    }

    /// <summary>Returns all indexed keys in comparer order.</summary>
    public IReadOnlyList<KeyValuePair<TKey, TValue>> Scan() => Scan(KeyRange<TKey>.All);

    /// <summary>Removes all entries from the index.</summary>
    public void Clear()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            values.Clear();
        }
    }

    /// <summary>Releases the index and rejects subsequent operations.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            values.Clear();
            disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
