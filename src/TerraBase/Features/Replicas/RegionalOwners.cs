using System.Collections.Concurrent;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Replicas;

internal sealed record RegionalTransfer(string Id, string Source, string Target, string SourceSession, string Phase, DateTimeOffset Deadline);

internal sealed record RegionalPrepared(string Source, string Target, long Epoch, string? Operation);

internal sealed record RegionalOwner(string Region, string Node, long Epoch, string? Previous = null,
    bool Activated = false, RegionalTransfer? Transfer = null, int Version = 0);

/// <summary>Observed ownership, independent of reachability and peers-file ordering.</summary>
internal sealed class RegionalOwners(TerraBaseOptions options)
{
    private readonly ConcurrentDictionary<string, RegionalOwner> _owners = new(StringComparer.Ordinal);
    private long _until;
    private int _draining;

    internal bool Writer => options.NatsUrl is null || options.PeersFile is null
        || (_owners.TryGetValue(options.Region, out var owner) && owner.Node == options.MachineId
            && Volatile.Read(ref _draining) == 0 && TimeProvider.System.GetTimestamp() < Volatile.Read(ref _until));

    internal RegionalOwner? Find(string region) => _owners.GetValueOrDefault(region);

    internal void Observe(RegionalOwner owner)
    {
        var previous = Find(owner.Region);
        if (owner.Region == options.Region && (previous is null || previous.Node != owner.Node || previous.Epoch != owner.Epoch))
        {
            // A former writer's unexpired renewal must never admit a new epoch before activation.
            Volatile.Write(ref _until, 0);
        }
        _owners[owner.Region] = owner;
    }

    internal void Renew(long until) => Volatile.Write(ref _until, until);

    internal void Drain()
    {
        Volatile.Write(ref _draining, 1);
        Volatile.Write(ref _until, 0);
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
