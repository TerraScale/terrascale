using TerraBase.Configuration;
using TerraBase.Engine.Cluster;

namespace TerraBase.Features.Regions;

/// <summary>The regions TerraBase serves now: the regions of the live members this node sees, its own included.</summary>
public interface IRegionCatalog
{
    /// <summary>Every serving region's id, ordered ordinally; never empty.</summary>
    ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken);
}

/// <summary><c>GET /v1/regions</c>: every region TerraBase serves now.</summary>
/// <param name="Regions">Region ids, ordered ordinally.</param>
public sealed record RegionsBody(IReadOnlyList<string> Regions);

/// <summary>
/// The catalog from membership (ts-orch's peers file in a cluster): a region serves while one of its nodes is listed, so a new
/// region appears once its first node joins, with nothing to configure.
/// </summary>
internal sealed class MembershipRegionCatalog(IMembership membership, TerraBaseOptions options) : IRegionCatalog
{
    public async ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken)
    {
        var view = await membership.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return [.. view.Members.Concat(view.Elsewhere.IsDefault ? [] : view.Elsewhere).Select(member => member.Region)
            .Append(options.Region)
            .Where(TerraBaseOptions.IsRegion)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }
}

/// <summary>Registers the region catalog.</summary>
public static class RegionsExtensions
{
    /// <summary>Adds <see cref="IRegionCatalog"/> from membership.</summary>
    public static IServiceCollection AddTerraBaseRegions(this IServiceCollection services) =>
        services.AddSingleton<IRegionCatalog, MembershipRegionCatalog>();
}
