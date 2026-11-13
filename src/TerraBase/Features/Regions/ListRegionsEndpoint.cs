using TerraBase.Hosting;
using TerraScale.Endpoints;
using TerraBase.Features.Callers;

namespace TerraBase.Features.Regions;

public sealed class ListRegionsEndpoint(IRegionCatalog catalog) : EndpointWithoutRequest<RegionsBody>
{
    public override void Configure()
    {
        Metadata(new Requires("catalog.read"));
        Get("/v1/regions");
        Name("listRegions");
        Summary("Lists the regions TerraBase serves");
        Description("regions: the id of every region with a live TerraBase node, ordered; a new region appears once its first node joins. homeRegion and replicaRegions of a database name these ids, and https://<id>.api.terrascale.tech reaches that region. Cacheable for 5 seconds.");
        Tags("Regions");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client);
    }

    public override async ValueTask<RegionsBody> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "public, max-age=5";
        return new RegionsBody(await catalog.ServingAsync(cancellationToken).ConfigureAwait(false));
    }
}
