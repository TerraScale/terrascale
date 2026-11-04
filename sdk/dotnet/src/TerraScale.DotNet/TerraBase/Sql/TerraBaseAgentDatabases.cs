using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A database-scoped API key. Key is returned once and can be passed to another client's ApiKey option.</summary>
/// <param name="Key">The secret API key.</param>
/// <param name="Id">The key ULID.</param>
/// <param name="ProjectId">Owning project ULID.</param>
/// <param name="Database">The only reachable database ULID.</param>
/// <param name="Access">read or read_write.</param>
/// <param name="ExpiresAt">The expiry instant, bounded by the parent key's expiry.</param>
public sealed record TerraBaseScopedKey(string Key, string Id, [property: JsonPropertyName("project_id")] string ProjectId, string Database, string Access, [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    /// <inheritdoc />
    public override string ToString() => $"Scoped API key {Id} [REDACTED] until {ExpiresAt:O}";
}

public sealed partial class TerraBaseSqlClient
{
    /// <summary>Creates a database. Optional TTL is 60 to 31,536,000 seconds. Expiry deletes the database, backups and data keys. Requires databases:write.</summary>
    public Task<TerraBaseManagedDatabase> CreateDatabaseAsync(string project, string name, int? ttlSeconds = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ValidateDatabaseTtl(ttlSeconds);
        return SendDatabaseAsync(project, HttpMethod.Post, DatabasesPath(project), new(name, TtlSeconds: ttlSeconds), cancellationToken);
    }

    /// <summary>
    /// Forks a consistent copy now, or restores an RFC 3339 instant inside the source's restore window.
    /// Source is a database name or ULID. TTL is 60 to 31,536,000 seconds; omitted TTL is never inherited.
    /// Requires databases:write.
    /// </summary>
    public Task<TerraBaseManagedDatabase> ForkDatabaseAsync(string project, string source, string name, DateTimeOffset? pointInTime = null,
        int? ttlSeconds = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ValidateDatabaseTtl(ttlSeconds);
        var path = DatabasePath(project, source) + (pointInTime is null ? "/branches" : "/point-in-time/restore");
        return SendDatabaseAsync(project, HttpMethod.Post, path, new(name, pointInTime, ttlSeconds), cancellationToken);
    }

    /// <summary>Sets a database's TTL to 60 to 31,536,000 seconds from now. Requires databases:write.</summary>
    public Task<TerraBaseManagedDatabase> SetDatabaseTtlAsync(string project, string database, int ttlSeconds, CancellationToken cancellationToken = default)
    {
        ValidateDatabaseTtl(ttlSeconds);
        return SendDatabaseAsync(project, HttpMethod.Put, DatabasePath(project, database) + "/ttl", new(TtlSeconds: ttlSeconds), cancellationToken);
    }

    /// <summary>Clears a database's TTL, returning ExpiresAt as null. Requires databases:write.</summary>
    public Task<TerraBaseManagedDatabase> ClearDatabaseTtlAsync(string project, string database, CancellationToken cancellationToken = default) =>
        SendDatabaseAsync(project, HttpMethod.Delete, DatabasePath(project, database) + "/ttl", null, cancellationToken);

    /// <summary>Deletes a database, its backups and data keys. Requires databases:write.</summary>
    public Task DeleteDatabaseAsync(string project, string database, CancellationToken cancellationToken = default) =>
        ProjectConnection(project).SendAsync(HttpMethod.Delete, DatabasePath(project, database), null, null, cancellationToken);

    /// <summary>
    /// Mints a key for one database ULID from this client's ApiKey; AccessTokenProvider cannot mint keys.
    /// Access is read or read_write. Project is required for organization keys and must match project keys.
    /// TTL is 60 to 2,592,000 seconds, default 3600, bounded by the parent. The key cannot manage databases or mint keys.
    /// Revoking or rotating the parent revokes its scoped keys. Refusals are never retried.
    /// </summary>
    public Task<TerraBaseScopedKey> CreateScopedKeyAsync(string database, string access, string? project = null, int? ttlSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (apiKeyTokens is null) throw new InvalidOperationException("CreateScopedKeyAsync requires the client's ApiKey; AccessTokenProvider cannot mint scoped keys.");
        TerraScaleIds.Validate(database);
        if (project is not null) TerraScaleIds.Validate(project);
        if (access is not ("read" or "read_write")) throw new ArgumentException("Access must be read or read_write.", nameof(access));
        if (ttlSeconds is < 60 or > 2_592_000) throw new ArgumentOutOfRangeException(nameof(ttlSeconds), "Scoped key TTL must be between 60 and 2,592,000 seconds.");
        return apiKeyTokens.CreateScopedKeyAsync(new(project, database, access, ttlSeconds), cancellationToken);
    }

    private async Task<TerraBaseManagedDatabase> SendDatabaseAsync(string project, HttpMethod method, string path,
        TerraBaseAgentDatabaseRequest? request, CancellationToken cancellationToken)
    {
        var json = TerraBaseJsonContext.Wire;
        return (await ProjectConnection(project).SendAsync(method, path,
            request is null ? null : TerraBaseConnection.Json(request, json.TerraBaseAgentDatabaseRequest),
            json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    private TerraBaseConnection ProjectConnection(string project)
    {
        if (connection is not null) return connection;
        TerraScaleIds.Validate(project);
        return projectConnections!.GetOrAdd(project, id => connect!(apiKeyTokens!.For(id)));
    }

    private static string DatabasesPath(string project) => $"/v1/projects/{TerraBaseConnection.Segment(project, nameof(project))}/databases";

    private static string DatabasePath(string project, string database) => DatabasesPath(project) + "/" + TerraBaseConnection.Segment(database, nameof(database));

    private static void ValidateDatabaseTtl(int? ttlSeconds)
    {
        if (ttlSeconds is < 60 or > 31_536_000) throw new ArgumentOutOfRangeException(nameof(ttlSeconds), "Database TTL must be between 60 and 31,536,000 seconds.");
    }
}

internal sealed record TerraBaseAgentDatabaseRequest(string? Name = null, DateTimeOffset? PointInTime = null, int? TtlSeconds = null);

internal sealed record TerraBaseScopedKeyRequest(string? Project, string Database, string Access, [property: JsonPropertyName("ttl_seconds")] int? TtlSeconds);
