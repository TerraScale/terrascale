namespace TerraScale.DotNet.TerraBase;

/// <summary>A collection's expiry member.</summary>
/// <param name="Path">JSON Pointer of 1 to 16 object members, for example <c>/expiresAt</c>; null when documents never expire.</param>
public sealed record TerraBaseCollectionTtl(string? Path);

public sealed partial class TerraBaseDatabase
{
    /// <summary>
    /// Makes documents expire once the member at <paramref name="path"/> holds a past RFC 3339 UTC time. The home region
    /// deletes them within about an hour; until then they stay readable. Requires documents:write. Sent once.
    /// </summary>
    public async Task<TerraBaseCollectionTtl> SetCollectionTtlAsync(string collection, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return (await connection.SendAsync(HttpMethod.Put, CollectionTtlPath(collection), TerraBaseConnection.Json(new TerraBaseCollectionTtl(path), Json.TerraBaseCollectionTtl),
            Json.TerraBaseCollectionTtl, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Reads the collection's expiry member; <see cref="TerraBaseCollectionTtl.Path"/> is null when none is set. Requires documents:read.</summary>
    public async Task<TerraBaseCollectionTtl> GetCollectionTtlAsync(string collection, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, CollectionTtlPath(collection), null, Json.TerraBaseCollectionTtl, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Clears the expiry member so documents no longer expire. Requires documents:write. Sent once.</summary>
    public async Task<TerraBaseCollectionTtl> ClearCollectionTtlAsync(string collection, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Delete, CollectionTtlPath(collection), null, Json.TerraBaseCollectionTtl, null, cancellationToken).ConfigureAwait(false)).Value;

    private string CollectionTtlPath(string collection) => CollectionPath(collection) + "/ttl";
}
