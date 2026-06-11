namespace TerraScale.DotNet;

/// <summary>
/// Client for real-time repository change subscriptions.
/// </summary>
public sealed class TerraScaleChangesClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleChangesClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Subscribes to document changes for a repository.
    /// </summary>
    public IAsyncEnumerable<TerraScaleRepositoryChange> SubscribeRepositoryAsync(
        string repositoryId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ValidateLimit(limit);
        return client.StreamServerSentEventsAsync<TerraScaleRepositoryChange>(
            BuildRepositoryEventsPath(repositoryId, limit),
            "document",
            cancellationToken);
    }

    /// <summary>
    /// Subscribes to document changes for a repository after validating a TQL source query.
    /// </summary>
    public IAsyncEnumerable<TerraScaleRepositoryChange> SubscribeTqlAsync(
        string repositoryId,
        string query,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ValidateLimit(limit);
        return client.StreamServerSentEventsAsync<TerraScaleRepositoryChange>(
            BuildTqlEventsPath(repositoryId, query, limit),
            "document",
            cancellationToken);
    }

    private static void ValidateLimit(int? limit)
    {
        if (limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than 0.");
        }
    }

    private static string BuildRepositoryEventsPath(string repositoryId, int? limit)
    {
        var path = $"/api/v1/repositories/{Escape(repositoryId)}/events";
        return limit is null ? path : $"{path}?limit={limit.Value}";
    }

    private static string BuildTqlEventsPath(string repositoryId, string query, int? limit)
    {
        var path = $"/api/v1/repositories/{Escape(repositoryId)}/tql/events?query={Escape(query)}";
        return limit is null ? path : $"{path}&limit={limit.Value}";
    }

    private static string Escape(string value) =>
        Uri.EscapeDataString(value.Trim());
}

/// <summary>
/// Document change event emitted by TerraScale repository streams.
/// </summary>
public sealed record TerraScaleRepositoryChange(
    string RepositoryId,
    string Repository,
    string Collection,
    string Id,
    string Type,
    string? ChangeToken,
    DateTimeOffset ObservedAt);
