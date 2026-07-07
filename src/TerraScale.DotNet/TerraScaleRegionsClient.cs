namespace TerraScale.DotNet;

/// <summary>
/// Client for TerraScale region catalog endpoints.
/// </summary>
public sealed class TerraScaleRegionsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleRegionsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists canonical TerraScale deployment regions.
    /// </summary>
    public async Task<IReadOnlyList<TerraScaleRegion>> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.SendAnonymousJsonAsync<TerraScaleRegionListResponse>(
            HttpMethod.Get,
            TerraScaleApiRoutes.RegionsList(),
            body: null,
            cancellationToken).ConfigureAwait(false);

        return response.Regions;
    }
}

internal sealed record TerraScaleRegionListResponse(IReadOnlyList<TerraScaleRegion> Regions);
