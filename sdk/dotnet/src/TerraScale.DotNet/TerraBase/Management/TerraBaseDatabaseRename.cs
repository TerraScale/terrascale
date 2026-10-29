namespace TerraScale.DotNet.TerraBase;

public sealed partial class TerraBaseProjectClient
{
    /// <summary>
    /// Renames a database at its home region, preserving its immutable ULID, data and placement. Use the immutable ULID
    /// for <paramref name="databaseId"/>. After convergence the old name stops resolving unless claimed again, so
    /// update name-based connection strings and obtain a new database handle using the returned name or ULID.
    /// Requires databases:write. Invalid names answer 400; an observed same-region duplicate answers 409. Concurrent cross-region claims converge; the smallest ULID keeps the name.
    /// Only documented pre-execution 503 refusals may be resent; ambiguous outcomes are not replayed. Existing handles keep their original route.
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
