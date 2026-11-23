using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Regions;

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RegionsBody))]
internal sealed partial class RegionsJsonContext : JsonSerializerContext;

internal sealed class ListRegionsEndpoint(RegionDirectory regions, TerraBaseOptions options) : EndpointWithoutRequest<RegionsBody>
{
    public override void Configure()
    {
        Get("/v1/regions");
        Name("listRegions");
        Summary("Lists the ids of the regions TerraBase serves, ordered; every region accepts reads and writes");
        Tags("Regions");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client);
    }

    public override ValueTask<RegionsBody> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new RegionsBody(options.Region, options.MachineId, regions.List()));
}
