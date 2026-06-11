namespace TerraScale.DotNet;

/// <summary>
/// Client for TerraScale database collection endpoints.
/// </summary>
public sealed class TerraScaleCollectionsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleCollectionsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists collections for a TerraScale database.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleCollectionSummary>> ListAsync(
        string databaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        return client.SendAsync<IReadOnlyList<TerraScaleCollectionSummary>>(
            HttpMethod.Get,
            $"/api/v1/databases/{Escape(databaseId)}/collections",
            body: null,
            cancellationToken);
    }

    private static string Escape(string value) =>
        Uri.EscapeDataString(value.Trim());
}
