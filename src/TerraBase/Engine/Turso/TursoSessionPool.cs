namespace TerraBase.Engine.Turso;

internal sealed class TursoSessionPool(int maxIdle, int maxIdlePerDatabase, int maxWriters)
{
    private readonly Lock _gate = new();

    private readonly LinkedList<(TursoDatabaseStore Store, TursoSession Session)> _idle = new();

    private readonly HashSet<TursoSession> _rented = [];
    private readonly Dictionary<TursoSession, TursoDatabaseStore> _owners = [];
    private readonly Dictionary<TursoDatabaseStore, int> _openByStore = [];
    private int _opening;
    private int _closing;
    private bool _stopped;
    private TaskCompletionSource? _closed;
    private List<Exception>? _stopFailures;

    private readonly Dictionary<TursoDatabaseStore, TaskCompletionSource> _paused = [];
    private readonly Dictionary<TursoDatabaseStore, TaskCompletionSource> _readersPaused = [];
    private readonly Dictionary<TursoDatabaseStore, TaskCompletionSource> _drains = [];
    private readonly Dictionary<TursoDatabaseStore, int> _failedCloses = [];

    private readonly LinkedList<TursoDatabaseStore> _writers = new();
    private readonly Dictionary<TursoDatabaseStore, LinkedListNode<TursoDatabaseStore>> _writerNodes = [];

    internal int MaxWriters => maxWriters;

    internal int OpenWriters
    {
        get
        {
            lock (_gate)
            {
                return _writers.Count;
            }
        }
    }

    internal List<TursoDatabaseStore>? UseWriter(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            if (_stopped || store.Closed)
            {
                return null;
            }

            if (_writerNodes.TryGetValue(store, out var node))
            {
                _writers.Remove(node);
                _writers.AddLast(node);
            }
            else
            {
                _writerNodes.Add(store, _writers.AddLast(store));
            }

            List<TursoDatabaseStore>? evicted = null;
            while (_writers.Count > maxWriters && _writers.First!.Value != store)
            {
                var victim = _writers.First.Value;
                _writers.RemoveFirst();
                _writerNodes.Remove(victim);
                if (!victim.Closed && !_paused.ContainsKey(victim))
                {
                    (evicted ??= []).Add(victim);
                }
            }

            return evicted;
        }
    }

    internal void ForgetWriter(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            if (_writerNodes.Remove(store, out var node))
            {
                _writers.Remove(node);
            }
        }
    }

    internal bool Paused(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            return _paused.ContainsKey(store) || _readersPaused.ContainsKey(store);
        }
    }

    internal void CloseIdleOf(TursoDatabaseStore store) => CloseIdle(store);

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
            _failedCloses[store] = _failedCloses.GetValueOrDefault(store) + 1;
            if (_drains.Remove(store, out var refused))
            {
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

    public void OpenFailed(TursoDatabaseStore store) => Update(() =>
    {
        _opening--;
        Closed(store);
    });

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

    public void Discard(TursoSession session)
    {
        lock (_gate)
        {
            _rented.Remove(session);
            _closing++;
        }

        Close(session);
    }

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
            }
        }

        Update(() => { });
        return closed;
    }

    public void Close(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            store.Closed = true;
        }

        CloseIdle(store);
    }

    public void Pause(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped || store.Closed, store);
            _paused.Add(store, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        CloseIdle(store);
    }

    public void PauseReaders(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped || store.Closed, store);
            _readersPaused.Add(store, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

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

    public int BusyOf(TursoDatabaseStore store, TursoSession? writer)
    {
        lock (_gate)
        {
            var busy = _openByStore.GetValueOrDefault(store) - CountOf(store);
            return writer is not null && _rented.Contains(writer) && _owners.GetValueOrDefault(writer) == store ? busy - 1 : busy;
        }
    }

    public int FailedClosesOf(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            return _failedCloses.GetValueOrDefault(store);
        }
    }

    public void RecordFailedClose(TursoDatabaseStore store)
    {
        lock (_gate)
        {
            _failedCloses[store] = _failedCloses.GetValueOrDefault(store) + 1;
        }
    }

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
                (failures ??= []).Add(error);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("closing database connections failed", failures);
        }
    }

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

internal sealed class StorePausedException(Task resumed) : Exception("the database is being rewritten")
{
    public Task Resumed { get; } = resumed;
}
