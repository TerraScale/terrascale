namespace TerraScale.DotNet;

/// <summary>
/// Client for TerraScale Query Language endpoints.
/// </summary>
public sealed class TerraScaleTqlClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleTqlClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Runs a TQL query against a repository.
    /// </summary>
    public Task<TerraScaleDocumentPage> QueryAsync(
        string repository,
        string query,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleDocumentPage>(
            HttpMethod.Post,
            $"/api/v1/repositories/{Escape(repository)}/tql",
            new TerraScaleTqlQuery(query, skip, take),
            cancellationToken);

    private static string Escape(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Uri.EscapeDataString(value.Trim());
    }
}

internal sealed record TerraScaleTqlQuery(
    string Query,
    int? Skip,
    int? Take);
