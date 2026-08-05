namespace TerraScale.DotNet;

/// <summary>
/// Client for version 3 project saved-query snapshot pagination.
/// </summary>
public sealed class TerraScaleSavedQueriesV3Client
{
    private readonly TerraScaleClient client;

    internal TerraScaleSavedQueriesV3Client(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists a mutation-safe snapshot page of saved queries. Pass <see cref="TerraScaleSavedQuerySnapshotPage.NextCursor"/>
    /// unchanged to continue the snapshot, and reload the collection after a conflict response.
    /// </summary>
    public Task<TerraScaleSavedQuerySnapshotPage> ListAsync(
        string projectId,
        string? cursor = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (take is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be between one and 500.");
        }

        return client.SendAsync<TerraScaleSavedQuerySnapshotPage>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectSavedQueriesV3List(projectId, cursor, take),
            body: null,
            cancellationToken);
    }
}

/// <summary>
/// A mutation-safe snapshot page of version 2 saved-query representations.
/// </summary>
public sealed record TerraScaleSavedQuerySnapshotPage(
    IReadOnlyList<TerraScaleSavedQueryV2> Items,
    string? NextCursor);
