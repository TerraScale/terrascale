using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Regions;

public interface IRegionCatalog
{
    ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken);
}

public sealed record RegionsBody(IReadOnlyList<string> Regions);

internal sealed class ServingRegionCatalog(TerraBaseOptions options) : IRegionCatalog
{
    public async ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken)
    {
        var nodes = options.PeersFile is null
            ? []
            : await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. nodes.Select(node => node.Region).Append(options.Region)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }
}

public static class RegionsExtensions
{
    public static IServiceCollection AddTerraBaseRegions(this IServiceCollection services) =>
        services.AddSingleton<IRegionCatalog, ServingRegionCatalog>();
}
