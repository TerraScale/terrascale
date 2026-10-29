namespace TerraScale.DotNet.TerraBase;

/// <summary>Public metadata of a project's S3-compatible backup destination; contains no credentials.</summary>
/// <param name="Endpoint">HTTPS S3-compatible endpoint.</param>
/// <param name="Region">S3 region.</param>
/// <param name="Bucket">Bucket name.</param>
/// <param name="Mode">both (platform and customer) or customer (customer only).</param>
/// <param name="UpdatedAt">Last successful save.</param>
public sealed record TerraBaseBackupDestination(Uri Endpoint, string Region, string Bucket, string Mode, DateTimeOffset UpdatedAt);

/// <summary>Customer backup destination configuration. Credentials are write-only; never log this payload.</summary>
/// <param name="Endpoint">HTTPS S3 endpoint.</param>
/// <param name="Region">S3 region.</param>
/// <param name="Bucket">Bucket name.</param>
/// <param name="AccessKeyId">Write-only access key id.</param>
/// <param name="SecretKey">Write-only secret key.</param>
/// <param name="Mode">both (platform and customer) or customer (customer only).</param>
public sealed record TerraBaseBackupDestinationRequest(string Endpoint, string Region, string Bucket, string AccessKeyId, string SecretKey, string Mode)
{
    /// <summary>Omits credentials and customer configuration from diagnostics.</summary>
    public override string ToString() => "TerraBase backup destination request (redacted)";
}

public sealed partial class TerraBaseProjectClient
{
    /// <summary>Reads public backup destination metadata. Requires project-wide databases:read; database-narrowed tokens receive 404.</summary>
    public async Task<TerraBaseBackupDestination> GetBackupDestinationAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/backup-destination", null, Json.TerraBaseBackupDestination, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Validates and saves an S3-compatible destination. Requires project-wide databases:write; sent once. Credentials are never returned.</summary>
    public async Task<TerraBaseBackupDestination> SetBackupDestinationAsync(TerraBaseBackupDestinationRequest destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return (await connection.SendAsync(HttpMethod.Put, path + "/backup-destination",
            TerraBaseConnection.Json(destination, Json.TerraBaseBackupDestinationRequest), Json.TerraBaseBackupDestination, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Deletes the destination configuration. Requires project-wide databases:write; sent once.</summary>
    public Task DeleteBackupDestinationAsync(CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, path + "/backup-destination", null, null, cancellationToken);
}
