namespace TerraScale.DotNet.TerraBase;

/// <summary>A field whose query would benefit from an index.</summary>
/// <param name="Field">Field as written.</param>
/// <param name="Reason">Reason for the suggestion.</param>
public sealed record TerraBaseIndexSuggestion(string Field, string Reason);

/// <summary>Validation result and engine plan; a failed check returns Ok false without throwing.</summary>
/// <param name="Ok">Whether SQL is valid.</param>
/// <param name="Error">Validation error, or null.</param>
/// <param name="Collection">Queried collection, or null for invalid SQL.</param>
/// <param name="HasLimit">Whether SQL has LIMIT.</param>
/// <param name="Plan">Engine plan steps.</param>
/// <param name="Indexes">Selected secondary index names.</param>
/// <param name="Suggestions">Unindexed fields.</param>
public sealed record TerraBaseSqlCheck(bool Ok, string? Error, string? Collection, bool HasLimit,
    IReadOnlyList<string> Plan, IReadOnlyList<string> Indexes, IReadOnlyList<TerraBaseIndexSuggestion> Suggestions);

/// <summary>A named SQL statement saved in a project.</summary>
/// <param name="Id">Canonical uppercase ULID.</param>
/// <param name="Name">Display name.</param>
/// <param name="Sql">Saved SQL.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Last update, or null.</param>
public sealed record TerraBaseSavedQuery(string Id, string Name, string Sql, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>Best-effort observations of a canonical query with literal values removed.</summary>
/// <param name="Id">SHA-256 shape identifier, not an entity ULID.</param>
/// <param name="Collection">Collection name.</param>
/// <param name="Shape">Canonical query.</param>
/// <param name="Queries">Observed query count.</param>
/// <param name="TotalLatencyMs">Total latency in milliseconds.</param>
/// <param name="MaxLatencyMs">Maximum latency in milliseconds.</param>
/// <param name="RowsScanned">Total rows scanned.</param>
/// <param name="LastSeenAt">Latest observation.</param>
/// <param name="LatencyBuckets">
/// Observed queries per power-of-two millisecond bucket: bucket 0 is under 1 ms, bucket i (1 to 18) under 2^i ms, bucket 19 the rest.
/// Null for statistics recorded before buckets existed.
/// </param>
/// <param name="P95LatencyMs">Upper bound of the bucket holding the 95th percentile, at most <paramref name="MaxLatencyMs"/>; null without buckets.</param>
public sealed record TerraBaseQueryShape(string Id, string Collection, string Shape, long Queries,
    double TotalLatencyMs, double MaxLatencyMs, long RowsScanned, DateTimeOffset LastSeenAt,
    IReadOnlyList<long>? LatencyBuckets = null, double? P95LatencyMs = null);

/// <summary>At most 128 shapes, ordered by query count, collection and shape id.</summary>
/// <param name="Items">Observed shapes.</param>
public sealed record TerraBaseQueryShapeList(IReadOnlyList<TerraBaseQueryShape> Items);

internal sealed record TerraBaseSavedQueryRequest(string Name, string Sql);

public sealed partial class TerraBaseDatabase
{
    /// <summary>Checks SQL without executing it; invalid SQL returns Ok false. Requires databases:read.</summary>
    public Task<TerraBaseSqlCheck> CheckSqlAsync(string sql, CancellationToken cancellationToken = default) =>
        PlanSqlAsync("check", sql, cancellationToken);

    /// <summary>Explains SQL without executing or metering it; invalid SQL throws 400. Requires databases:read.</summary>
    public Task<TerraBaseSqlCheck> ExplainSqlAsync(string sql, CancellationToken cancellationToken = default) =>
        PlanSqlAsync("explain", sql, cancellationToken);

    /// <summary>Lists best-effort query shape statistics, including pending primary observations. Requires databases:read.</summary>
    public async Task<TerraBaseQueryShapeList> ListQueryShapesAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/query-shapes", null, Json.TerraBaseQueryShapeList, null, cancellationToken).ConfigureAwait(false)).Value;

    private async Task<TerraBaseSqlCheck> PlanSqlAsync(string operation, string sql, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sql);
        return (await connection.SendAsync(HttpMethod.Post, path + "/sql/" + operation,
            TerraBaseConnection.Json(new TerraBaseSqlRequest(sql), Json.TerraBaseSqlRequest), Json.TerraBaseSqlCheck, null, cancellationToken).ConfigureAwait(false)).Value;
    }
}

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
