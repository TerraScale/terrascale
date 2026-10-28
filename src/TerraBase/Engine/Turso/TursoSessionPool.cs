namespace TerraBase.Engine.Turso;

/// <summary>
/// The idle sessions of every database of one engine, bounded per database and in total. When the
/// total is reached, pooling a session closes the least recently pooled one, whichever database it
/// belongs to, so databases that are no longer used do not keep connections and file handles open.
/// </summary>
/// <remarks>
/// ponytail: one lock and a linear scan (at most <c>maxIdle</c> entries) per rent, and fixed caps;
/// index idle sessions by database, or make the caps settings, if profiles show either matters.
/// </remarks>
internal sealed class TursoSessionPool(int maxIdle, int maxIdlePerDatabase)
{
    // Guards everything below and TursoDatabaseStore.Closed, so a session is always in exactly one
    // place: idle here, rented by one transaction, or closed. Sessions are opened and closed outside
    // it: closing the last connection to a WAL database checkpoints it.
    private readonly Lock _gate = new();

    // Least recently pooled first.
    private readonly LinkedList<(TursoDatabaseStore Store, TursoSession Session)> _idle = new();

    // Every connection outside _idle: held by a transaction, being opened for one, or being closed outside _gate
    // (evicted, discarded, or idle when its store or the pool closed). After StopAsync, the last of them to go
    // completes the stop; until then the engine keeps its data directory.
    private readonly HashSet<TursoSession> _rented = [];
    private readonly Dictionary<TursoSession, TursoDatabaseStore> _owners = [];
    private readonly Dictionary<TursoDatabaseStore, int> _openByStore = [];
    private int _opening;
    private int _closing;
    private bool _stopped;
    private TaskCompletionSource? _closed;
    private List<Exception>? _stopFailures;

    // A healthy rewrite's stores: they rent, open and pool nothing until resumed, and their drain completes once no native
    // handle of theirs is left. Failed native closes stay counted per store and refuse every drain.
    private readonly Dictionary<TursoDatabaseStore, TaskCompletionSource> _paused = [];
    private readonly Dictionary<TursoDatabaseStore, TaskCompletionSource> _readersPaused = [];
    private readonly Dictionary<TursoDatabaseStore, TaskCompletionSource> _drains = [];
    private readonly Dictionary<TursoDatabaseStore, int> _failedCloses = [];

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
    public int IdleCountOf(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            return CountOf(store);
        }
    }

    internal bool HasOpenHandles(string path)
    {
        lock (_gate)
        {
            return _openByStore.Keys.Any(store => string.Equals(store.FilePath, path, StringComparison.Ordinal));
        }
    }

    internal void OpenExternal(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped || store.Closed, store);
            ThrowIfPaused(store);
            Opened(store);
        }
    }

    internal void CloseExternal(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            Closed(store);
        }
    }

    private void Opened(TursoDatabaseStore store) => _openByStore[store] = _openByStore.GetValueOrDefault(store) + 1;

    private void Closed(TursoDatabaseStore store, Exception? failure = null)
    {
        if (failure is not null)
        {
            // Failed native closure remains an outstanding handle until process restart.
            _failedCloses[store] = _failedCloses.GetValueOrDefault(store) + 1;
            if (_drains.Remove(store, out var refused))
            {
                // Never drains now: wake the waiter to refuse.
                refused.TrySetResult();
            }

            return;
        }

        if (_openByStore[store] == 1)
        {
            _openByStore.Remove(store);
            if (_drains.Remove(store, out var drained))
            {
                drained.TrySetResult();
            }
        }
        else
        {
            _openByStore[store]--;
        }
    }

    /// <summary>
    /// Takes the most recently pooled idle session of <paramref name="store"/>, or <see langword="null"/>
    /// when it has none; the caller then opens one and passes it to <see cref="Adopt"/>, or calls
    /// <see cref="OpenFailed"/>. Throws when the store is closed or the pool stopped.
    /// </summary>
    public TursoSession? Rent(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped || store.Closed, store);
            ThrowIfPaused(store);
            if (_readersPaused.TryGetValue(store, out var readers))
            {
                throw new StorePausedException(readers.Task);
            }
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
            Opened(store);
            return null;
        }
    }

    /// <summary>
    /// Counts an opening for a session that must be new, never an idle one: the store's writer
    /// (<see cref="TursoDatabaseStore"/>). The caller then calls <see cref="Adopt"/> or <see cref="OpenFailed"/>.
    /// </summary>
    public void OpenNew(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped || store.Closed, store);
            ThrowIfPaused(store);
            _opening++;
            Opened(store);
        }
    }

    /// <summary>
    /// Hands a newly opened <paramref name="session"/> to its renter, or closes it and throws when
    /// <paramref name="store"/> was closed, or the pool stopped, while it opened.
    /// </summary>
    public TursoSession Adopt(TursoDatabaseStore store, TursoSession session)
    {
        lock (_gate)
        {
            if (!_stopped && !store.Closed)
            {
                _opening--;
                _rented.Add(session);
                _owners.Add(session, store);
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
            Update(() =>
            {
                _opening--;
                Closed(store, failure);
            }, failure);
        }

        throw new ObjectDisposedException(store.GetType().FullName);
    }

    /// <summary>Ends an opening that <see cref="Rent"/> started and that produced no session.</summary>
    public void OpenFailed(TursoDatabaseStore store) => Update(() =>
    {
        _opening--;
        Closed(store);
    });

    /// <summary>
    /// Returns an untouched session without eviction or connection disposal. On failure the caller still owns it.
    /// Safe on a request thread when cancelled queued work never started using the connection.
    /// </summary>
    public bool TryReturn(TursoDatabaseStore store, TursoSession session)
    {
        lock (_gate)
        {
            if (_stopped || store.Closed || _paused.ContainsKey(store) || _idle.Count >= maxIdle || CountOf(store) >= maxIdlePerDatabase)
            {
                return false;
            }

            _rented.Remove(session);
            _idle.AddLast((store, session));
            return true;
        }
    }

    /// <summary>Pools a session whose work has ended, or closes it; may close the least recently pooled session.</summary>
    public void Return(TursoDatabaseStore store, TursoSession session)
    {
        TursoSession? evicted = null;
        var pooled = false;
        lock (_gate)
        {
            _rented.Remove(session);
            if (!_stopped && !store.Closed && !_paused.ContainsKey(store) && CountOf(store) < maxIdlePerDatabase)
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
    public void Discard(TursoSession session)
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
        List<TursoSession> idle;
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
    public void Close(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            store.Closed = true;
        }

        CloseIdle(store);
    }

    /// <summary>
    /// Pauses <paramref name="store"/> for a healthy rewrite and closes its idle sessions: renters wait for
    /// <see cref="Resume"/>, external readers are refused, and returned sessions close instead of pooling.
    /// </summary>
    public void Pause(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped || store.Closed, store);
            _paused.Add(store, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        CloseIdle(store);
    }

    /// <summary>Stops new pooled readers before draining existing readers, while writers and rewrite replay can continue.</summary>
    public void PauseReaders(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped || store.Closed, store);
            _readersPaused.Add(store, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    /// <summary>Ends <see cref="Pause"/>: waiting renters retry.</summary>
    public void Resume(TursoDatabaseStore store)
    {
        TaskCompletionSource? paused;
        TaskCompletionSource? readers;
        lock (_gate)
        {
            _ = _paused.Remove(store, out paused);
            _ = _readersPaused.Remove(store, out readers);
        }

        paused?.TrySetResult();
        readers?.TrySetResult();
    }

    /// <summary>
    /// Connections of <paramref name="store"/> held by a transaction, opening or closing, besides <paramref name="writer"/>: what
    /// a drain would still wait for. A healthy rewrite waits for none before it takes the writer, so no writer queues behind it.
    /// </summary>
    public int BusyOf(TursoDatabaseStore store, TursoSession? writer)
    {
        lock (_gate)
        {
            var busy = _openByStore.GetValueOrDefault(store) - CountOf(store);
            return writer is not null && _rented.Contains(writer) && _owners.GetValueOrDefault(writer) == store ? busy - 1 : busy;
        }
    }

    /// <summary>Native closes of <paramref name="store"/>'s connections that failed in this process: they may still be open.</summary>
    public int FailedClosesOf(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            return _failedCloses.GetValueOrDefault(store);
        }
    }

    /// <summary>Counts a failed native close of a connection <paramref name="store"/>'s owner opened outside the pool.</summary>
    public void RecordFailedClose(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            _failedCloses[store] = _failedCloses.GetValueOrDefault(store) + 1;
        }
    }

    /// <summary>
    /// Completes once no connection of <paramref name="store"/> is open, opening or closing, external readers included, or
    /// early when one of its native closes fails (<see cref="FailedClosesOf"/>).
    /// </summary>
    public Task DrainedAsync(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            if (!_openByStore.ContainsKey(store))
            {
                return Task.CompletedTask;
            }

            if (!_drains.TryGetValue(store, out var drained))
            {
                _drains[store] = drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return drained.Task;
        }
    }

    private void ThrowIfPaused(TursoDatabaseStore store)
    {
        if (_paused.TryGetValue(store, out var paused))
        {
            throw new StorePausedException(paused.Task);
        }
    }

    private void CloseIdle(TursoDatabaseStore store)
    {
        var closing = new List<TursoSession>();
        lock (_gate)
        {
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
    private void CloseAll(List<TursoSession> sessions)
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
    private void Close(TursoSession session)
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
            Update(() =>
            {
                _closing--;
                var owner = _owners[session];
                _owners.Remove(session);
                Closed(owner, failure);
            }, failure);
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

    private int CountOf(TursoDatabaseStore store)
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

/// <summary>A healthy rewrite paused the store; retry once <see cref="Resumed"/> completes.</summary>
internal sealed class StorePausedException(Task resumed) : Exception("the database is being rewritten")
{
    public Task Resumed { get; } = resumed;
}
