namespace TerraScale.DotNet.TerraBase;

public sealed partial class TerraBaseProjectClient
{
    public async Task<TerraBaseManagedDatabase> RenameDatabaseAsync(string databaseId, string name, CancellationToken cancellationToken = default)
    {
        TerraScaleIds.Validate(databaseId);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var body = TerraBaseConnection.Json(new RequestBody { Name = name }, Json.RequestBody);
        return (await connection.SendAsync(HttpMethod.Patch, path + "/databases/" + TerraBaseConnection.Segment(databaseId, nameof(databaseId)),
            body, Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }
}
