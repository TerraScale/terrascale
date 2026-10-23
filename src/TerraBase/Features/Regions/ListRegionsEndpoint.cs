using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Regions;

/// <summary>
/// <c>GET /v1/regions</c>: the region catalog, every region TerraBase serves now. Unauthenticated, so the dashboard, SDKs and
/// sign-up offer the same regions a database can be placed in; every region answers the same list.
/// </summary>
public sealed class ListRegionsEndpoint(IRegionCatalog catalog) : EndpointWithoutRequest<RegionsBody>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/v1/regions");
        Name("listRegions");
        Summary("Lists the regions TerraBase serves");
        Description("regions: the id of every region with a live TerraBase node, ordered; a new region appears once its first node joins. homeRegion and replicaRegions of a database name these ids, and https://<id>.api.terrascale.tech reaches that region. Cacheable for 5 seconds.");
        Tags("Regions");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<RegionsBody> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "public, max-age=5";
        return new RegionsBody(await catalog.ServingAsync(cancellationToken).ConfigureAwait(false));
    }
}
