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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        TerraScalePagination.ValidateOptional(skip, take);
        return client.SendAsync<TerraScaleDocumentPage, TerraScaleDocumentPage>(
            HttpMethod.Post,
            $"/api/v1/repositories/{Escape(repository)}/tql",
            new TerraScaleTqlQuery(query, skip, take),
            preferredRegion: null,
            configureRequest: null,
            client.ObserveDocumentPage,
            cancellationToken);
    }

    /// <summary>
    /// Runs a query built with <see cref="TerraScaleQuery"/> against a repository.
    /// </summary>
    public Task<TerraScaleDocumentPage> QueryAsync(
        string repository,
        TerraScaleQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return QueryAsync(
            repository,
            query.ToTql(),
            query.SkipCount,
            query.TakeCount,
            cancellationToken);
    }

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
