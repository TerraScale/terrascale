namespace TerraScale.DotNet.TerraBase;

public sealed partial class TerraBaseProjectClient
{
    /// <summary>
    /// Renames a database at its home region, preserving its immutable ULID, data and placement. Use the immutable ULID
    /// for <paramref name="databaseId"/>. Availability is held pending the owning API rollout. The old name stops resolving (404), so
    /// update name-based connection strings and obtain a new database handle using the returned name or ULID.
    /// Requires databases:write. Invalid names answer 400; a name already used in the project answers 409.
    /// Sent once, without retries. Existing database handles keep their original route.
    /// </summary>
    public async Task<TerraBaseManagedDatabase> RenameDatabaseAsync(string databaseId, string name, CancellationToken cancellationToken = default)
    {
        TerraScaleIds.Validate(databaseId);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var body = TerraBaseConnection.Json(new RequestBody { Name = name }, Json.RequestBody);
        return (await connection.SendAsync(HttpMethod.Patch, path + "/databases/" + TerraBaseConnection.Segment(databaseId, nameof(databaseId)),
            body, Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }
}
