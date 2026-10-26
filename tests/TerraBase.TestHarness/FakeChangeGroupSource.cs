using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;

namespace TerraBase.Testing;

/// <summary>
/// One test machine's change groups, in memory, as the CDC tailer reads them from <c>turso_cdc</c>: the test adds a
/// group after it committed the same deltas locally, and peers' acknowledgements are kept per database and peer node.
/// Every read and ack names the incarnation of the machine's copy that <c>incarnation</c> reports (null: not held),
/// so a refill the groups' sender names is the copy anti-entropy digests; another incarnation is refused.
/// </summary>
internal sealed class FakeChangeGroupSource(Func<TenantContext, CancellationToken, ValueTask<Ulid?>> incarnation) : IChangeGroupSource
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TenantContext, int> _readCounts = [];
    private readonly Dictionary<TenantContext, Func<CancellationToken, Task>> _afterEmptyReads = [];
    private readonly Dictionary<TenantContext, List<OriginGroup>> _groups = [];
    private readonly Dictionary<(TenantContext Database, ReplicaId Peer), long> _ackedBy = [];
    private readonly HashSet<(TenantContext Database, ReplicaId Peer)> _refills = [];
    private readonly Dictionary<(TenantContext Database, ReplicaId Peer, Ulid Incarnation), Hlc> _stableBy = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _acked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _beforeRead;
    private event Action<TenantContext>? Changed;

    public Action? AfterListing { get; set; }

    // A stale discovery candidate can be announced before its catalog placement or local copy exists.
    public void Announce(params TenantContext[] databases)
    {
        foreach (var database in databases)
        {
            lock (_gate)
            {
                _ = _groups.TryAdd(database, []);
            }

            Changed?.Invoke(database);
        }
    }

    /// <summary>
    /// Drops the groups through <paramref name="changeId"/> as retention does, just before the next read: a trim landing
    /// between a sender's checks. A peer acknowledged before them then has to refill.
    /// </summary>
    public void CutBeforeNextRead(TenantContext database, long changeId) => _beforeRead = () =>
    {
        lock (_gate)
        {
            _ = _groups[database].RemoveAll(group => group.ChangeId <= changeId);
        }
    };

    /// <summary>Appends a group of <paramref name="deltas"/> with the next change id, and returns that id.</summary>
    public long Add(TenantContext database, params ChangeDelta[] deltas)
    {
        TaskCompletionSource changed;
        long id;
        lock (_gate)
        {
            var groups = _groups.TryGetValue(database, out var known) ? known : _groups[database] = [];
            id = groups.Count == 0 ? 1 : groups[^1].ChangeId + 1;
            groups.Add(new OriginGroup(id, deltas));
            (changed, _changed) = (_changed, new(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        Changed?.Invoke(database with { Plan = PlanTier.Hobby });
        changed.SetResult();
        return id;
    }

    public int ReadCount(TenantContext database)
    {
        lock (_gate)
        {
            return _readCounts.GetValueOrDefault(database);
        }
    }

    // Runs outside the gate after the next empty snapshot, before that snapshot returns to the sender.
    public void AfterNextEmptyRead(TenantContext database, Func<CancellationToken, Task> afterRead)
    {
        lock (_gate)
        {
            _afterEmptyReads.Add(database, afterRead);
        }
    }

    /// <summary>
    /// Marks <paramref name="peer"/>'s row as a copied one: its cursor requires a refill, as storage reports a foreign scope,
    /// until an ack of this copy succeeds.
    /// </summary>
    public void RequireRefill(TenantContext database, ReplicaId peer)
    {
        lock (_gate)
        {
            _ = _refills.Add((database, peer));
        }
    }

    /// <summary>Whether <paramref name="peer"/>'s cursor still requires a refill.</summary>
    public bool RequiresRefill(TenantContext database, ReplicaId peer)
    {
        lock (_gate)
        {
            return _refills.Contains((database, peer));
        }
    }

    /// <summary>The last change id <paramref name="peer"/> acknowledged, or 0.</summary>
    public long AckedBy(TenantContext database, ReplicaId peer)
    {
        lock (_gate)
        {
            return _ackedBy.GetValueOrDefault((database, peer));
        }
    }

    public async IAsyncEnumerable<TenantContext> ListDatabasesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<TenantContext> databases;
        lock (_gate)
        {
            databases = [.. _groups.Keys];
        }

        foreach (var database in databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return database;
        }

        AfterListing?.Invoke();
        await Task.CompletedTask;
    }

    public async ValueTask<SourceGroups?> ReadAsync(
        TenantContext database, Ulid expected, long after, int maxGroups, int maxBytes, CancellationToken cancellationToken)
    {
        if (await incarnation(database, cancellationToken) != expected)
        {
            return null;
        }

        Interlocked.Exchange(ref _beforeRead, null)?.Invoke();
        SourceGroups snapshot;
        Func<CancellationToken, Task>? afterEmptyRead = null;
        lock (_gate)
        {
            _readCounts[database] = _readCounts.GetValueOrDefault(database) + 1;
            List<OriginGroup> page = [];
            var bytes = 0;
            foreach (var group in _groups.GetValueOrDefault(database, []).Where(group => group.ChangeId > after).Take(maxGroups))
            {
                bytes += group.Deltas.Sum(delta => DocumentStateEncoding.Encode(delta.State).Length);
                if (page.Count > 0 && bytes > maxBytes)
                {
                    break;
                }

                page.Add(group);
            }

            snapshot = new SourceGroups(expected, page, _groups.GetValueOrDefault(database) is not [var first, ..] || first.ChangeId <= after + 1);
            if (page.Count == 0)
            {
                _ = _afterEmptyReads.Remove(database, out afterEmptyRead);
            }
        }

        if (afterEmptyRead is not null)
        {
            await afterEmptyRead(cancellationToken);
        }

        return snapshot;
    }

    public async ValueTask<SourceCursor?> AckedAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken)
    {
        if (await incarnation(database, cancellationToken) is not { } current)
        {
            return null;
        }

        lock (_gate)
        {
            return new SourceCursor(current, _ackedBy.GetValueOrDefault((database, peer)))
            {
                RequiresRefill = _refills.Contains((database, peer)),
                Stable = _stableBy.GetValueOrDefault((database, peer, current)),
            };
        }
    }

    public async ValueTask<bool> AckAsync(TenantContext database, Ulid expected, ReplicaId peer, long changeId, Hlc stable, CancellationToken cancellationToken)
    {
        if (await incarnation(database, cancellationToken) != expected)
        {
            return false;
        }

        TaskCompletionSource acked;
        lock (_gate)
        {
            _ackedBy[(database, peer)] = Math.Max(_ackedBy.GetValueOrDefault((database, peer)), changeId);
            _stableBy[(database, peer, expected)] = new Hlc(Math.Max(_stableBy.GetValueOrDefault((database, peer, expected)).Value, stable.Value));
            _ = _refills.Remove((database, peer));
            (acked, _acked) = (_acked, new(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        acked.SetResult();
        return true;
    }

    /// <summary>
    /// Completes once <paramref name="peer"/> has acknowledged <paramref name="changeId"/> or more: the sender records an ack
    /// only after the peer's commit, so a test that saw the peer apply a group waits here before it reads the ack.
    /// </summary>
    public async Task UntilAckedAsync(TenantContext database, ReplicaId peer, long changeId, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task next;
            lock (_gate)
            {
                if (_ackedBy.GetValueOrDefault((database, peer)) >= changeId)
                {
                    return;
                }

                next = _acked.Task;
            }

            await next.WaitAsync(cancellationToken);
        }
    }

    // Fakes hold no store, so every proof is taken as established; proof behavior is exercised against real sources.
    public ValueTask<bool> ProveAsync(TenantContext database, Ulid incarnation, long from, long through, IReadOnlySet<ReplicaId> held, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);

    public IDisposable Subscribe(Action<TenantContext> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var subscription = new Subscription(this, changed);
        Changed += changed;
        return subscription;
    }

    private sealed class Subscription(FakeChangeGroupSource source, Action<TenantContext> changed) : IDisposable
    {
        private Action<TenantContext>? _changed = changed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _changed, null) is { } callback)
            {
                source.Changed -= callback;
            }
        }
    }

    public Task ChangedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _changed.Task.WaitAsync(cancellationToken);
        }
    }

    public async ValueTask<bool?> RetainsAsync(TenantContext database, Ulid expected, long after, CancellationToken cancellationToken)
    {
        if (await incarnation(database, cancellationToken) != expected)
        {
            return null;
        }

        lock (_gate)
        {
            return _groups.GetValueOrDefault(database) is not [var first, ..] || first.ChangeId <= after + 1;
        }
    }

    public async ValueTask<bool> RecordStableAsync(TenantContext database, Ulid expected, ReplicaId peer, Hlc stable, CancellationToken cancellationToken)
    {
        if (await incarnation(database, cancellationToken) != expected)
        {
            return false;
        }

        lock (_gate)
        {
            _stableBy[(database, peer, expected)] = new Hlc(Math.Max(_stableBy.GetValueOrDefault((database, peer, expected)).Value, stable.Value));
        }

        return true;
    }

    public async ValueTask<long?> RetainedAfterAsync(TenantContext database, Ulid expected, long? through, long head, CancellationToken cancellationToken)
    {
        if (await incarnation(database, cancellationToken) != expected)
        {
            return null;
        }

        lock (_gate)
        {
            var groups = _groups.GetValueOrDefault(database, []);
            var floor = groups.Count == 0 ? 0 : groups[0].ChangeId - 1;
            return through is { } bound ? Math.Max(floor, groups.LastOrDefault(group => group.ChangeId <= bound)?.ChangeId ?? 0) : floor;
        }
    }

    public async ValueTask<long?> HeadAsync(TenantContext database, Ulid expected, CancellationToken cancellationToken)
    {
        if (await incarnation(database, cancellationToken) != expected)
        {
            return null;
        }

        lock (_gate)
        {
            return _groups.TryGetValue(database, out var groups) && groups.Count > 0 ? groups[^1].ChangeId : 0L;
        }
    }
}
