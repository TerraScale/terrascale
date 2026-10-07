using TerraBase.Features.Backup;

namespace TerraBase.Testing;

/// <summary>
/// The backup lease store with NATS KV semantics, in memory, shared by the members of a test: create-if-absent, renew at
/// the held revision, and keys that expire <see cref="Ttl"/> after their last write by <see cref="TimeProvider"/>.
/// <see cref="Partition"/> cuts a member off, as a lost NATS connection would.
/// </summary>
internal sealed class FakeLeases(TimeProvider time) : ILeaseStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Holder, ulong Revision, DateTimeOffset Written)> _leases = [];
    private readonly HashSet<string> _partitioned = [];
    private ulong _revision;

    public TimeSpan Ttl { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Every call of the member named <paramref name="node"/> fails from now on.</summary>
    public void Partition(string node)
    {
        lock (_gate)
        {
            _partitioned.Add(node);
        }
    }

    /// <summary>The node name of each held lease's holder, by key.</summary>
    public IReadOnlyDictionary<string, string> Holders()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            return _leases.Where(lease => now - lease.Value.Written < Ttl)
                .ToDictionary(lease => lease.Key, lease => lease.Value.Holder[..lease.Value.Holder.IndexOf('/', StringComparison.Ordinal)]);
        }
    }

    public Task<ulong?> TryAcquireAsync(string key, string holder, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Reachable(holder);
            if (_leases.TryGetValue(key, out var lease) && time.GetUtcNow() - lease.Written < Ttl)
            {
                return Task.FromResult<ulong?>(null);
            }

            _leases[key] = (holder, ++_revision, time.GetUtcNow());
            return Task.FromResult<ulong?>(_revision);
        }
    }

    public Task<ulong?> TryRenewAsync(string key, string holder, ulong revision, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Reachable(holder);
            if (!_leases.TryGetValue(key, out var lease) || lease.Revision != revision || time.GetUtcNow() - lease.Written >= Ttl)
            {
                return Task.FromResult<ulong?>(null);
            }

            _leases[key] = (holder, ++_revision, time.GetUtcNow());
            return Task.FromResult<ulong?>(_revision);
        }
    }

    public Task ReleaseAsync(string key, ulong revision, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(key, out var lease) && lease.Revision == revision)
            {
                Reachable(lease.Holder);
                _leases.Remove(key);
            }

            return Task.CompletedTask;
        }
    }

    private void Reachable(string holder)
    {
        if (_partitioned.Contains(holder[..holder.IndexOf('/', StringComparison.Ordinal)]))
        {
            throw new IOException("partitioned from NATS");
        }
    }
}
