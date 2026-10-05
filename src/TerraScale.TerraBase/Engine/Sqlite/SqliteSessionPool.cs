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

    // Every connection outside _idle: held by a transaction, being opened for one, or being closed outside _gate
    // (evicted, discarded, or idle when its store or the pool closed). After StopAsync, the last of them to go
    // completes the stop; until then the engine keeps its data directory.
    private readonly HashSet<SqliteSession> _rented = [];
    private int _opening;
    private int _closing;
    private bool _stopped;
    private TaskCompletionSource? _closed;
    private List<Exception>? _stopFailures;

    /// <summary>The engine-wide bound on idle sessions.</summary>
    public int MaxIdle => maxIdle;

    /// <summary>Connections not idle in the pool: held by transactions, being opened, or being closed.</summary>
    public int OpenCount
    {
        get
        {
            lock (_gate)
            {
                return _rented.Count + _opening + _closing;
            }
        }
    }

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
    /// when it has none; the caller then opens one and passes it to <see cref="Adopt"/>, or calls
    /// <see cref="OpenFailed"/>. Throws when the store is closed or the pool stopped.
    /// </summary>
    public SqliteSession? Rent(SqliteDatabaseStore store)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped || store.Closed, store);
            for (var node = _idle.Last; node is not null; node = node.Previous)
            {
                if (node.Value.Store == store)
                {
                    _idle.Remove(node);
                    _rented.Add(node.Value.Session);
                    return node.Value.Session;
                }
            }

            _opening++;
            return null;
        }
    }

    /// <summary>
    /// Hands a newly opened <paramref name="session"/> to its renter, or closes it and throws when
    /// <paramref name="store"/> was closed, or the pool stopped, while it opened.
    /// </summary>
    public SqliteSession Adopt(SqliteDatabaseStore store, SqliteSession session)
    {
        lock (_gate)
        {
            if (!_stopped && !store.Closed)
            {
                _opening--;
                _rented.Add(session);
                return session;
            }
        }

        Exception? failure = null;
        try
        {
            session.Dispose();
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            Update(() => _opening--, failure);
        }

        throw new ObjectDisposedException(store.GetType().FullName);
    }

    /// <summary>Ends an opening that <see cref="Rent"/> started and that produced no session.</summary>
    public void OpenFailed() => Update(() => _opening--);

    /// <summary>
    /// Returns an untouched session without eviction or connection disposal. On failure the caller still owns it.
    /// Safe on a request thread when cancelled queued work never started using the connection.
    /// </summary>
    public bool TryReturn(SqliteDatabaseStore store, SqliteSession session)
    {
        lock (_gate)
        {
            if (_stopped || store.Closed || _idle.Count >= maxIdle || CountOf(store) >= maxIdlePerDatabase)
            {
                return false;
            }

            _rented.Remove(session);
            _idle.AddLast((store, session));
            return true;
        }
    }

    /// <summary>Pools a session whose work has ended, or closes it; may close the least recently pooled session.</summary>
    public void Return(SqliteDatabaseStore store, SqliteSession session)
    {
        SqliteSession? evicted = null;
        var pooled = false;
        lock (_gate)
        {
            _rented.Remove(session);
            if (!_stopped && !store.Closed && CountOf(store) < maxIdlePerDatabase)
            {
                _idle.AddLast((store, session));
                pooled = true;
                if (_idle.Count > maxIdle)
                {
                    evicted = _idle.First!.Value.Session;
                    _idle.RemoveFirst();
                    _closing++;
                }
            }
            else
            {
                _closing++;
            }
        }

        if (evicted is not null)
        {
            Close(evicted);
        }

        if (!pooled)
        {
            Close(session);
        }
    }

    /// <summary>Closes a rented session instead of pooling it.</summary>
    public void Discard(SqliteSession session)
    {
        lock (_gate)
        {
            _rented.Remove(session);
            _closing++;
        }

        Close(session);
    }

    /// <summary>
    /// Stops renting and pooling and closes idle sessions on the calling I/O worker. Completes only once every
    /// rented session is returned and closed and every opening finishes; no active connection is closed by force.
    /// Closing failures are reported after all counted sessions have ended.
    /// </summary>
    public Task StopAsync()
    {
        List<SqliteSession> idle;
        Task closed;
        lock (_gate)
        {
            _stopped = true;
            idle = [.. _idle.Select(entry => entry.Session)];
            _idle.Clear();
            _closing += idle.Count;
            _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            closed = _closed.Task;
        }

        foreach (var session in idle)
        {
            try
            {
                Close(session);
            }
            catch (Exception)
            {
                // Close recorded the failure on the stop task; still attempt every remaining session.
            }
        }

        // Completes an empty stop too. Later returns/openings also record close failures before completing it.
        Update(() => { });
        return closed;
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

            _closing += closing.Count;
        }

        CloseAll(closing);
    }

    // Closes every session of a batch counted in _closing, even when some fail, so none is left uncounted and
    // unclosed; the failures are reported once all were attempted.
    private void CloseAll(List<SqliteSession> sessions)
    {
        List<Exception>? failures = null;
        foreach (var session in sessions)
        {
            try
            {
                Close(session);
            }
            catch (Exception error)
            {
                // Whatever the failure: the next session must still be closed, or it stays open and counted forever.
                (failures ??= []).Add(error);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("closing database connections failed", failures);
        }
    }

    // Closes a session counted in _closing outside _gate, and stops counting it once it is closed, even if closing failed.
    private void Close(SqliteSession session)
    {
        Exception? failure = null;
        try
        {
            session.Dispose();
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            Update(() => _closing--, failure);
        }
    }

    // Completes the stop only after the last native handle is closed, including failed provider closes.
    private void Update(Action change, Exception? failure = null)
    {
        TaskCompletionSource? closed = null;
        AggregateException? errors = null;
        lock (_gate)
        {
            change();
            if (_stopped && failure is not null)
            {
                (_stopFailures ??= []).Add(failure);
            }
            if (_stopped && _rented.Count == 0 && _opening == 0 && _closing == 0)
            {
                closed = _closed;
                if (_stopFailures is not null)
                {
                    errors = new AggregateException("closing database connections failed", _stopFailures);
                }
            }
        }

        if (errors is not null)
        {
            closed?.TrySetException(errors);
        }
        else
        {
            closed?.TrySetResult();
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
