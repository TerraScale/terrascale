using TerraScale.DotNet.Generated;

#pragma warning disable CS1591

namespace TerraScale.DotNet;

/// <summary>Client for the published database backup endpoints.</summary>
public sealed class TerraScaleBackupsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleBackupsClient(TerraScaleClient client) => this.client = client;

    /// <summary>Returns native backup readiness for a database.</summary>
    public Task<BackupStatusResponse> ListAsync(string database, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        return client.SendJsonAsync(
            HttpMethod.Get,
            $"/api/v1/databases/{Escape(database)}/backups",
            TerraScaleContractJsonContext.Default.BackupStatusResponse,
            cancellationToken);
    }

    /// <summary>Restores a backup into a new database.</summary>
    public Task<DatabaseRecord> RestoreAsync(
        string database,
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        return client.SendJsonAsync(
            HttpMethod.Post,
            $"/api/v1/databases/{Escape(database)}/backups/restore",
            request,
            TerraScaleContractJsonContext.Default.RestoreRequest,
            TerraScaleContractJsonContext.Default.DatabaseRecord,
            cancellationToken);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value.Trim());
}
