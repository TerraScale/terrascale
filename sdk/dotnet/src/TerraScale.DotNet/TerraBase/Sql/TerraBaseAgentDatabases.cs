using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseScopedKey(string Key, string Id, [property: JsonPropertyName("project_id")] string ProjectId, string Database, string Access, [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"Scoped API key {Id} [REDACTED] until {ExpiresAt:O}";
}

public sealed partial class TerraBaseSqlClient
{
    public Task<TerraBaseManagedDatabase> CreateDatabaseAsync(string project, string name, int? ttlSeconds = null,
        TerraBaseDatabasePlacement? placement = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ValidateDatabaseTtl(ttlSeconds);
        return SendDatabaseAsync(project, HttpMethod.Post, DatabasesPath(project),
            new(name, TtlSeconds: ttlSeconds, Regions: placement?.Regions, AllRegions: placement?.AllRegions), cancellationToken);
    }

    public Task<TerraBaseManagedDatabase> CreateDatabaseAsync(string project, string name, TerraBaseConsistencyMode defaultConsistency, int? ttlSeconds = null,
        TerraBaseDatabasePlacement? placement = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ValidateDatabaseTtl(ttlSeconds);
        return SendDatabaseAsync(project, HttpMethod.Post, DatabasesPath(project),
            new(name, TtlSeconds: ttlSeconds, Regions: placement?.Regions, AllRegions: placement?.AllRegions, DefaultConsistency: defaultConsistency), cancellationToken);
    }

    public Task<TerraBaseManagedDatabase> SetDatabaseConsistencyAsync(string project, string database, TerraBaseConsistencyMode defaultConsistency,
        CancellationToken cancellationToken = default) =>
        SendDatabaseAsync(project, HttpMethod.Patch, DatabasePath(project, database), new(DefaultConsistency: defaultConsistency), cancellationToken);

    public Task<TerraBaseManagedDatabase> SetDatabasePlacementAsync(string project, string database, TerraBaseDatabasePlacement placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return SendDatabaseAsync(project, HttpMethod.Patch, DatabasePath(project, database),
            new(Regions: placement.Regions, AllRegions: placement.AllRegions), cancellationToken);
    }

    public async Task<TerraBaseManagedDatabase> ForkDatabaseAsync(string project, string source, string name, DateTimeOffset? pointInTime = null,
        int? ttlSeconds = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ValidateDatabaseTtl(ttlSeconds);
        var path = DatabasePath(project, source) + (pointInTime is null ? "/branches" : "/point-in-time/restore");
        if (pointInTime is null)
            return await SendDatabaseAsync(project, HttpMethod.Post, path, new(name, pointInTime, ttlSeconds), cancellationToken).ConfigureAwait(false);
        var json = TerraBaseJsonContext.Wire;
        var operation = await ProjectConnection(project).SendAsync(HttpMethod.Post, path,
            TerraBaseConnection.Json(new TerraBaseAgentDatabaseRequest(name, pointInTime, ttlSeconds), json.TerraBaseAgentDatabaseRequest),
            json.TerraBaseRestoreOperation, null, cancellationToken).ConfigureAwait(false);
        return await TerraBaseRestore.CompleteAsync(ProjectConnection(project), DatabasePath(project, source),
            "/v1/projects/" + TerraScalePaths.Segment(project, nameof(project)),
            operation.Value, json.TerraBaseManagedDatabase, cancellationToken).ConfigureAwait(false);
    }

    public Task<TerraBaseManagedDatabase> SetDatabaseTtlAsync(string project, string database, int ttlSeconds, CancellationToken cancellationToken = default)
    {
        ValidateDatabaseTtl(ttlSeconds);
        return SendDatabaseAsync(project, HttpMethod.Put, DatabasePath(project, database) + "/ttl", new(TtlSeconds: ttlSeconds), cancellationToken);
    }

    public Task<TerraBaseManagedDatabase> ClearDatabaseTtlAsync(string project, string database, CancellationToken cancellationToken = default) =>
        SendDatabaseAsync(project, HttpMethod.Delete, DatabasePath(project, database) + "/ttl", null, cancellationToken);

    public Task DeleteDatabaseAsync(string project, string database, CancellationToken cancellationToken = default) =>
        ProjectConnection(project).SendAsync(HttpMethod.Delete, DatabasePath(project, database), null, null, cancellationToken);

    public Task<TerraBaseScopedKey> CreateScopedKeyAsync(string database, string access, string? project = null, int? ttlSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (projectApiKey is not { Tokens: var tokens }) throw new InvalidOperationException("CreateScopedKeyAsync requires the client's ApiKey; AccessTokenProvider cannot mint scoped keys.");
        TerraScaleIds.Validate(database);
        if (project is not null) TerraScaleIds.Validate(project);
        if (access is not ("read" or "read_write")) throw new ArgumentException("Access must be read or read_write.", nameof(access));
        if (ttlSeconds is < 60 or > 2_592_000) throw new ArgumentOutOfRangeException(nameof(ttlSeconds), "Scoped key TTL must be between 60 and 2,592,000 seconds.");
        return tokens.CreateScopedKeyAsync(new(project, database, access, ttlSeconds), cancellationToken);
    }

    private async Task<TerraBaseManagedDatabase> SendDatabaseAsync(string project, HttpMethod method, string path,
        TerraBaseAgentDatabaseRequest? request, CancellationToken cancellationToken, TimeSpan? requestTimeout = null)
    {
        var json = TerraBaseJsonContext.Wire;
        return (await ProjectConnection(project).SendAsync(method, path,
            request is null ? null : TerraBaseConnection.Json(request, json.TerraBaseAgentDatabaseRequest),
            json.TerraBaseManagedDatabase, null, cancellationToken, requestTimeout).ConfigureAwait(false)).Value;
    }

    private TerraBaseConnection ProjectConnection(string project)
    {
        if (connection is not null) return connection;
        TerraScaleIds.Validate(project);
        return (projectApiKey ?? throw new InvalidOperationException("The SQL client has no project credential.")).For(project);
    }

    private static string DatabasesPath(string project) => $"/v1/projects/{TerraScalePaths.Segment(project, nameof(project))}/databases";

    private static string DatabasePath(string project, string database) => DatabasesPath(project) + "/" + TerraScalePaths.Segment(database, nameof(database));

    private static void ValidateDatabaseTtl(int? ttlSeconds)
    {
        if (ttlSeconds is < 60 or > 31_536_000) throw new ArgumentOutOfRangeException(nameof(ttlSeconds), "Database TTL must be between 60 and 31,536,000 seconds.");
    }
}

internal sealed record TerraBaseAgentDatabaseRequest(string? Name = null, DateTimeOffset? PointInTime = null, int? TtlSeconds = null,
    IReadOnlyList<string>? Regions = null, bool? AllRegions = null, TerraBaseConsistencyMode? DefaultConsistency = null);

internal sealed record TerraBaseScopedKeyRequest(string? Project, string Database, string Access, [property: JsonPropertyName("ttl_seconds")] int? TtlSeconds);
