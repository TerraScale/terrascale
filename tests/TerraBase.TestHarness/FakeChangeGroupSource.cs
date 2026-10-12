using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;

namespace TerraBase.Testing;

/// <summary>
/// One test machine's change groups, in memory, as the CDC tailer (R3) will read them from <c>turso_cdc</c>: the test adds a
/// group after it committed the same deltas locally, and peers' acknowledgements are kept per database and peer node.
/// </summary>
internal sealed class FakeChangeGroupSource : IChangeGroupSource
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TenantContext, List<OriginGroup>> _groups = [];
    private readonly Dictionary<(TenantContext Database, ReplicaId Peer), long> _ackedBy = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _acked = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        changed.SetResult();
        return id;
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

        await Task.CompletedTask;
    }

    public ValueTask<IReadOnlyList<OriginGroup>> ReadAsync(TenantContext database, long after, int maxGroups, int maxBytes, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
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

            return ValueTask.FromResult<IReadOnlyList<OriginGroup>>(page);
        }
    }

    public ValueTask<long> AckedAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken) => ValueTask.FromResult(AckedBy(database, peer));

    public ValueTask AckAsync(TenantContext database, ReplicaId peer, long changeId, CancellationToken cancellationToken)
    {
        TaskCompletionSource acked;
        lock (_gate)
        {
            _ackedBy[(database, peer)] = Math.Max(_ackedBy.GetValueOrDefault((database, peer)), changeId);
            (acked, _acked) = (_acked, new(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        acked.SetResult();
        return ValueTask.CompletedTask;
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

    public Task ChangedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _changed.Task.WaitAsync(cancellationToken);
        }
    }
}
