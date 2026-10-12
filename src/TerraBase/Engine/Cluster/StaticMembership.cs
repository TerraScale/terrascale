using System.Collections.Immutable;
using TerraBase.Configuration;

namespace TerraBase.Engine.Cluster;

/// <summary>
/// A cell that never changes: this member alone off Fly, or the members a test lists. Every member is live, this member
/// always holds its lease, and the view never changes.
/// </summary>
internal sealed class StaticMembership(TerraBaseOptions options, ImmutableArray<ClusterMember> others = default) : IMembership
{
    private readonly ClusterView _view = new(1, [.. (others.IsDefault ? [] : others)
        .Append(new ClusterMember(options.MachineId, string.Empty, options.Region, string.Empty))
        .OrderBy(member => member.Name, StringComparer.Ordinal)]);

    public ClusterMember Self => _view.Members.First(member => member.Name == options.MachineId);

    public bool Dynamic => false;

    public bool HoldsLease => true;

    public TimeSpan Fence => TimeSpan.Zero;

    public ValueTask<ClusterView> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_view);

    public ValueTask LeaveAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
