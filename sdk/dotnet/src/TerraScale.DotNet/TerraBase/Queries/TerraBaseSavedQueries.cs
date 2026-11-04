namespace TerraScale.DotNet.TerraBase;

/// <summary>A named SQL statement saved in a project.</summary>
/// <param name="Id">Canonical uppercase ULID.</param>
/// <param name="Name">Display name.</param>
/// <param name="Sql">Saved SQL.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Last update, or null.</param>
public sealed record TerraBaseSavedQuery(string Id, string Name, string Sql, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

internal sealed record TerraBaseSavedQueryRequest(string Name, string Sql);

public sealed partial class TerraBaseProjectClient
{
    /// <summary>Saves a valid SQL statement under a 1–128 character name. Requires databases:write; sent once.</summary>
    public Task<TerraBaseSavedQuery> CreateSavedQueryAsync(string name, string sql, CancellationToken cancellationToken = default) =>
        SaveQueryAsync(HttpMethod.Post, path + "/saved-queries", name, sql, cancellationToken);

    /// <summary>Lists queries in creation order. Limit 1–100, default 20; pass Next as after. Requires databases:read.</summary>
    public async Task<TerraBasePage<TerraBaseSavedQuery>> ListSavedQueriesAsync(string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/saved-queries" + TerraBaseConnection.Query(("after", after), ("limit", TerraBaseConnection.Number(limit))),
            null, Json.TerraBasePageTerraBaseSavedQuery, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Reads a saved query by canonical ULID. Requires databases:read.</summary>
    public async Task<TerraBaseSavedQuery> GetSavedQueryAsync(string queryId, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, SavedQueryPath(queryId), null, Json.TerraBaseSavedQuery, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Replaces a saved query's name and SQL. Requires databases:write; sent once.</summary>
    public Task<TerraBaseSavedQuery> UpdateSavedQueryAsync(string queryId, string name, string sql, CancellationToken cancellationToken = default) =>
        SaveQueryAsync(HttpMethod.Put, SavedQueryPath(queryId), name, sql, cancellationToken);

    /// <summary>Deletes a saved query. Requires databases:write; sent once.</summary>
    public Task DeleteSavedQueryAsync(string queryId, CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, SavedQueryPath(queryId), null, null, cancellationToken);

    private async Task<TerraBaseSavedQuery> SaveQueryAsync(HttpMethod method, string target, string name, string sql, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(sql);
        return (await connection.SendAsync(method, target,
            TerraBaseConnection.Json(new TerraBaseSavedQueryRequest(name, sql), Json.TerraBaseSavedQueryRequest), Json.TerraBaseSavedQuery, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    private string SavedQueryPath(string queryId) => path + "/saved-queries/" + TerraScaleIds.Validate(queryId);
}
