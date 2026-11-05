using System.Collections.Concurrent;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Replicas;

internal sealed record RegionalOwner(string Region, string Node, long Epoch);

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

    internal void Observe(RegionalOwner owner) => _owners[owner.Region] = owner;

    internal void Renew() => Volatile.Write(ref _until, TimeProvider.System.GetTimestamp() + 6 * TimeProvider.System.TimestampFrequency);

    internal void Drain() => Volatile.Write(ref _draining, 1);

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
