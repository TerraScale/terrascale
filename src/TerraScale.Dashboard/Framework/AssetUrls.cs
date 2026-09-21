using System.Collections.Frozen;
using Microsoft.AspNetCore.StaticAssets;

namespace TerraScale.Dashboard;

/// <summary>Uses the SDK's content-fingerprinted routes and precompressed assets.
/// Development keeps original URLs so dotnet watch can serve changed files.</summary>
public static class AssetUrls
{
    private static FrozenDictionary<string, string> _urls = FrozenDictionary<string, string>.Empty;

    public static void Initialize(IEndpointRouteBuilder routes, bool development)
    {
        _urls = development ? FrozenDictionary<string, string>.Empty : routes.DataSources
            .SelectMany(source => source.Endpoints)
            .Select(endpoint => endpoint.Metadata.GetMetadata<StaticAssetDescriptor>())
            .OfType<StaticAssetDescriptor>()
            .Select(asset => (Asset: asset, Label: asset.Properties.FirstOrDefault(p => p.Name == "label")?.Value))
            .Where(item => item.Label is not null)
            .GroupBy(item => "/" + item.Label, StringComparer.Ordinal)
            .ToFrozenDictionary(group => group.Key, group => "/" + group.First().Asset.Route, StringComparer.Ordinal);
    }

    public static string Url(string path) => _urls.GetValueOrDefault(path, path);
}
