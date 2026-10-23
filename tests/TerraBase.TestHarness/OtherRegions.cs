using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Engine.Cluster;

namespace TerraBase.Testing;

/// <summary>
/// A static membership that also lists one member in each of <paramref name="regions"/>, which StaticMembership never reports:
/// the server's region catalog then serves them, without starting machines there.
/// </summary>
public sealed class OtherRegions(params string[] regions)
{
    /// <summary>Registers the membership; pass as a server's services.</summary>
    public void Register(IServiceCollection services) =>
        services.AddSingleton<IMembership>(provider => new View(provider.GetRequiredService<TerraBaseOptions>(), regions));

    private sealed class View(TerraBaseOptions options, string[] regions) : IMembership
    {
        private readonly ClusterView _view = new(1, [new(options.MachineId, string.Empty, options.Region, string.Empty)],
            [.. regions.Select((region, index) => new ClusterMember($"m{index}", string.Empty, region, string.Empty))]);

        public ClusterMember Self => _view.Members[0];

        public bool Dynamic => false;

        public bool HoldsLease => true;

        public TimeSpan Fence => TimeSpan.Zero;

        public ValueTask<ClusterView> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_view);

        public ValueTask LeaveAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
