namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseBackupDestination(Uri Endpoint, string Region, string Bucket, string Mode, DateTimeOffset UpdatedAt);

public sealed record TerraBaseBackupDestinationRequest(string Endpoint, string Region, string Bucket, string AccessKeyId, string SecretKey, string Mode)
{
    public override string ToString() => "TerraBase backup destination request (redacted)";
}

public sealed partial class TerraBaseProjectClient
{
    public async Task<TerraBaseBackupDestination> GetBackupDestinationAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/backup-destination", null, Json.TerraBaseBackupDestination, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseBackupDestination> SetBackupDestinationAsync(TerraBaseBackupDestinationRequest destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return (await connection.SendAsync(HttpMethod.Put, path + "/backup-destination",
            TerraBaseConnection.Json(destination, Json.TerraBaseBackupDestinationRequest), Json.TerraBaseBackupDestination, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    public Task DeleteBackupDestinationAsync(CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, path + "/backup-destination", null, null, cancellationToken);
}
