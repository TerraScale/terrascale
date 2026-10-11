using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

// Uses the existing Atlas region catalog consumed by the Go dashboard.
// SDK-owned Atlas Documents discovery (TS-507) remains an external gate.
public sealed class TerraBaseDiscovery(HttpClient http) : ITerraBaseEndpointResolver
{
    public async ValueTask<Uri> ResolveAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("https://atlas.terrascale.tech/v1/discovery", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await PlatformJson.ReadAsync(response.Content, cancellationToken);
        foreach (var region in document.RootElement.GetProperty("regions").EnumerateArray())
        {
            if (region.GetProperty("health").GetString() == "down") continue;
            if (Uri.TryCreate(region.GetProperty("api").GetString(), UriKind.Absolute, out var origin) &&
                origin.Scheme == "https" && origin.IsDefaultPort && origin.UserInfo.Length == 0 &&
                origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0 &&
                origin.Host.EndsWith(".api.terrascale.tech", StringComparison.OrdinalIgnoreCase))
                return origin;
        }
        throw new HttpRequestException("Atlas published no serving TerraBase origin.");
    }
}
