using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>Client for TerraScale database collection endpoints.</summary>
public sealed class TerraScaleCollectionsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleCollectionsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>Lists the distinct collection names occupied by a database.</summary>
    public async Task<IReadOnlyList<string>> ListAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        var response = await client.SendAsync<TerraScaleCollectionsResponse>(
            HttpMethod.Get,
            $"/api/v1/databases/{Escape(database)}/collections",
            body: null,
            cancellationToken).ConfigureAwait(false);
        return response.Collections;
    }

    private static string Escape(string value) =>
        Uri.EscapeDataString(value.Trim());
}

internal sealed record TerraScaleCollectionsResponse([property: JsonRequired] IReadOnlyList<string> Collections) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Collections is null || Collections.Any(static collection => string.IsNullOrWhiteSpace(collection)))
        {
            throw new JsonException("TerraScale collection lists require nonblank collection names.");
        }
    }
}
