using System.Collections.Concurrent;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Replicas;

internal sealed record RegionalTransfer(string Id, string Source, string Target, string SourceSession, string Phase, DateTimeOffset Deadline, long PreparedEpoch = 0);

internal sealed record RegionalPrepared(string Source, string Target, long Epoch, string? Operation);

internal sealed record RegionalOwner(string Region, string Node, long Epoch, string? Previous = null,
    bool Activated = false, RegionalTransfer? Transfer = null, int Version = 0, RegionalPrepared? Prepared = null);

internal sealed record WriterTerm(long Epoch, CancellationToken Lost);

internal sealed class RegionalOwners(TerraBaseOptions options, TimeProvider time) : IDisposable
{
    private readonly ConcurrentDictionary<string, RegionalOwner> _owners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _unconfirmed = new(StringComparer.Ordinal);
    private readonly Lock _leaseGate = new();
    private WriterLease _lease = new(time);
    private long _until;

    internal WriterTerm? Term()
    {
        lock (_leaseGate)
        {
            return Writer ? new WriterTerm(_owners.TryGetValue(options.Region, out var owner) ? owner.Epoch : 0, _lease.Token) : null;
        }
    }

    public void Dispose()
    {
        lock (_leaseGate)
        {
            _lease.Dispose();
        }
    }

    private sealed class WriterLease : IDisposable
    {
        internal CancellationTokenSource Source { get; }
        internal CancellationToken Token { get; }

        internal WriterLease(TimeProvider time)
        {
            Source = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
            Token = Source.Token;
        }

        public void Dispose()
        {
            Source.Cancel();
            Source.Dispose();
        }
    }
    private int _draining;

    internal bool Writer => options.NatsUrl is null || options.PeersFile is null
        || (_owners.TryGetValue(options.Region, out var owner) && owner.Node == options.MachineId
            && Volatile.Read(ref _draining) == 0 && time.GetTimestamp() < Volatile.Read(ref _until));

    internal bool Drained => Volatile.Read(ref _draining) == 1;

    internal RegionalOwner? Find(string region) => _owners.GetValueOrDefault(region);

    internal IReadOnlyCollection<string> Regions => [.. _owners.Keys];

    internal RegionalOwner? Confirmed(string region) => _unconfirmed.ContainsKey(region) ? null : Find(region);

    internal void Unconfirm(string region) => _unconfirmed[region] = 0;

    internal void Observe(RegionalOwner owner)
    {
        var previous = Find(owner.Region);
        if (owner.Region == options.Region && (previous is null || previous.Node != owner.Node || previous.Epoch != owner.Epoch))
        {
            LoseWriter();
        }
        _owners[owner.Region] = owner;
        _ = _unconfirmed.TryRemove(owner.Region, out _);
    }

    internal void Renew(long until)
    {
        lock (_leaseGate)
        {
            if (_lease.Token.IsCancellationRequested)
            {
                _lease.Dispose();
                _lease = new WriterLease(time);
            }

            Volatile.Write(ref _until, until);
            var remaining = time.GetElapsedTime(time.GetTimestamp(), until);
            _lease.Source.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
    }

    private void LoseWriter()
    {
        lock (_leaseGate)
        {
            Volatile.Write(ref _until, 0);
            _lease.Source.Cancel();
        }
    }

    internal void Drain()
    {
        Volatile.Write(ref _draining, 1);
        LoseWriter();
    }

    internal void Resume() => Volatile.Write(ref _draining, 0);

    internal async ValueTask<IReadOnlyList<ServingNode>> NodesAsync(CancellationToken cancellationToken)
    {
        if (options.PeersFile is null)
        {
            return [];
        }

        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. nodes.GroupBy(node => node.Region, StringComparer.Ordinal)
            .Select(group => group.FirstOrDefault(node => node.Name == Find(group.Key)?.Node) ?? group.First())];
    }
}
