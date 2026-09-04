using System.Text.Json.Serialization;

#pragma warning disable CS1591

namespace TerraScale.DotNet;

/// <summary>Client for the published database backup endpoints.</summary>
public sealed class TerraScaleBackupsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleBackupsClient(TerraScaleClient client) => this.client = client;

    /// <summary>Returns Litestream readiness for a database.</summary>
    public Task<TerraScaleBackupReadiness> ListAsync(string database, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        return client.SendAsync<TerraScaleBackupReadiness>(
            HttpMethod.Get,
            $"/api/v1/databases/{Escape(database)}/backups",
            body: null,
            cancellationToken);
    }

    /// <summary>Restores a backup into a new database.</summary>
    public Task<TerraScaleDatabaseRecord> RestoreAsync(
        string database,
        TerraScaleRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        return client.SendAsync<TerraScaleDatabaseRecord>(
            HttpMethod.Post,
            $"/api/v1/databases/{Escape(database)}/backups/restore",
            request,
            cancellationToken);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value.Trim());
}

/// <summary>Litestream readiness for one database.</summary>
public sealed record TerraScaleBackupReadiness(
    [property: JsonRequired] bool Ready,
    [property: JsonRequired] bool Streaming);

/// <summary>Request to restore a backup into a new database.</summary>
public sealed record TerraScaleRestoreRequest(
    [property: JsonRequired] string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? At = null);

#pragma warning restore CS1591
