namespace TerraScale.TerraBase.Engine.Sqlite;

/// <summary>
/// The idle sessions of every database of one engine, bounded per database and in total. When the
/// total is reached, pooling a session closes the least recently pooled one, whichever database it
/// belongs to, so databases that are no longer used do not keep connections and file handles open.
/// </summary>
/// <remarks>
/// ponytail: one lock and a linear scan (at most <c>maxIdle</c> entries) per rent, and fixed caps;
/// index idle sessions by database, or make the caps settings, if profiles show either matters.
/// </remarks>
internal sealed class SqliteSessionPool(int maxIdle, int maxIdlePerDatabase)
{
    // Guards everything below and SqliteDatabaseStore.Closed, so a session is always in exactly one
    // place: idle here, rented by one transaction, or closed. Sessions are opened and closed outside
    // it: closing the last connection to a WAL database checkpoints it.
    private readonly Lock _gate = new();

    // Least recently pooled first.
    private readonly LinkedList<(SqliteDatabaseStore Store, SqliteSession Session)> _idle = new();

    /// <summary>The engine-wide bound on idle sessions.</summary>
    public int MaxIdle => maxIdle;

    /// <summary>Idle sessions of every database.</summary>
    public int IdleCount
    {
        get
        {
            lock (_gate)
            {
                return _idle.Count;
            }
        }
    }

    /// <summary>Idle sessions of <paramref name="store"/>.</summary>
    public int IdleCountOf(SqliteDatabaseStore store)
    {
        lock (_gate)
        {
            return CountOf(store);
        }
    }

    /// <summary>
    /// Takes the most recently pooled idle session of <paramref name="store"/>, or <see langword="null"/>
    /// when it has none. Throws when the store is closed.
    /// </summary>
    public SqliteSession? Rent(SqliteDatabaseStore store)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(store.Closed, store);
            for (var node = _idle.Last; node is not null; node = node.Previous)
            {
                if (node.Value.Store == store)
                {
                    _idle.Remove(node);
                    return node.Value.Session;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Hands a newly opened <paramref name="session"/> to its renter, or closes it and throws when
    /// <paramref name="store"/> was closed while it opened.
    /// </summary>
    public SqliteSession Adopt(SqliteDatabaseStore store, SqliteSession session)
    {
        lock (_gate)
        {
            if (!store.Closed)
            {
                return session;
            }
        }

        session.Dispose();
        throw new ObjectDisposedException(store.GetType().FullName);
    }

    /// <summary>Pools a session whose work has ended, or closes it; may close the least recently pooled session.</summary>
    public void Return(SqliteDatabaseStore store, SqliteSession session)
    {
        SqliteSession? close = session;
        lock (_gate)
        {
            if (!store.Closed && CountOf(store) < maxIdlePerDatabase)
            {
                _idle.AddLast((store, session));
                close = null;
                if (_idle.Count > maxIdle)
                {
                    close = _idle.First!.Value.Session;
                    _idle.RemoveFirst();
                }
            }
        }

        close?.Dispose();
    }

    /// <summary>Closes <paramref name="store"/>: its idle sessions are closed and it rents and pools no more.</summary>
    public void Close(SqliteDatabaseStore store)
    {
        var closing = new List<SqliteSession>();
        lock (_gate)
        {
            store.Closed = true;
            for (var node = _idle.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Store == store)
                {
                    closing.Add(node.Value.Session);
                    _idle.Remove(node);
                }

                node = next;
            }
        }

        foreach (var session in closing)
        {
            session.Dispose();
        }
    }

    private int CountOf(SqliteDatabaseStore store)
    {
        var count = 0;
        foreach (var (owner, _) in _idle)
        {
            if (owner == store)
            {
                count++;
            }
        }

        return count;
    }
}
