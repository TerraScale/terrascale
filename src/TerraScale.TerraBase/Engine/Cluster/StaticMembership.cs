using TerraBase.Configuration;

namespace TerraBase.Engine.Cluster;

/// <summary>
/// The static <c>peers</c> list (tests and single-node runs): every listed member is live, this member always holds its
/// lease, and the view never changes. Without <c>peers</c> the cell is this member alone.
/// </summary>
internal sealed class StaticMembership(TerraBaseOptions options) : IMembership
{
    private readonly ClusterView _view = new(1, [.. (options.Peers ?? new Dictionary<string, Uri> { [options.NodeName] = null! })
        .Select(peer => new ClusterMember(peer.Key, peer.Value?.ToString() ?? string.Empty, options.Region, string.Empty))
        .OrderBy(member => member.Name, StringComparer.Ordinal)]);

    public ClusterMember Self => _view.Members.First(member => member.Name == options.NodeName);

    public bool Dynamic => false;

    public bool HoldsLease => true;

    public TimeSpan Fence => TimeSpan.Zero;

    public ValueTask<ClusterView> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_view);

    public ValueTask LeaveAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
