namespace TerraScale.DotNet;

/// <summary>
/// Client for TerraScale database endpoints.
/// </summary>
public sealed class TerraScaleDatabasesClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleDatabasesClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists databases visible to the current credentials.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleDatabaseSummary>> ListAsync(
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<IReadOnlyList<TerraScaleDatabaseSummary>>(
            HttpMethod.Get,
            $"/api/v1/databases?start={skip}&take={take}",
            body: null,
            cancellationToken);

    /// <summary>
    /// Creates a TerraScale database.
    /// </summary>
    public Task<TerraScaleDatabaseDetails> CreateAsync(
        string name,
        int replicationFactor = 1,
        IReadOnlyDictionary<string, string>? settings = null,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleDatabaseDetails>(
            HttpMethod.Post,
            "/api/v1/databases",
            new TerraScaleCreateDatabaseRequest(name, replicationFactor, settings),
            cancellationToken);
}
