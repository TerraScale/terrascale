using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

// api.terrascale.tech is TerraBase's global origin: Fly answers it in a serving region and TerraBase replays a request
// for a database placed elsewhere (terrascale docs/terrabase/region-identity.md). There is no Atlas discovery.
public sealed class TerraBaseDiscovery : ITerraBaseEndpointResolver
{
    public static readonly Uri Origin = new("https://api.terrascale.tech/");

    public ValueTask<Uri> ResolveAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Origin);
}
