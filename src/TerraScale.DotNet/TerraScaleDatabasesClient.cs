using System.Text.Json.Serialization;

#pragma warning disable CS1591

namespace TerraScale.DotNet;

/// <summary>
/// Client for TerraScale database endpoints.
/// </summary>
public sealed class TerraScaleDatabasesClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleDatabasesClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists databases visible to the current credentials.
    /// </summary>
    public async Task<IReadOnlyList<TerraScaleDatabaseRecord>> ListAsync(
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        TerraScalePagination.Validate(skip, take);
        var response = await client.SendAsync<TerraScaleDatabaseListResponse>(
            HttpMethod.Get,
            "/api/v1/databases",
            body: null,
            cancellationToken).ConfigureAwait(false);
        return response.Databases;
    }

    /// <summary>Creates a database with explicit placement and residency.</summary>
    public Task<TerraScaleDatabaseRecord> CreateAsync(
        string name,
        IReadOnlyList<string> placement,
        string? residency = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(placement);
        if (placement.Count is < 1 or > 20 || placement.Any(static region => region is not (TerraScaleRegions.Gru or TerraScaleRegions.Sfo or TerraScaleRegions.Fra)))
        {
            throw new ArgumentException("Placement must contain between 1 and 20 canonical TerraScale regions.", nameof(placement));
        }
        return client.SendAsync<TerraScaleDatabaseRecord>(HttpMethod.Post, "/api/v1/databases", new TerraScaleDirectCreateDatabaseRequest(name.Trim(), placement, residency), cancellationToken);
    }

    /// <summary>
    /// Gets database details by TerraScale database id.
    /// </summary>
    public Task<TerraScaleDatabaseRecord> GetAsync(
        string databaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        return client.SendAsync<TerraScaleDatabaseRecord>(
            HttpMethod.Get,
            $"/api/v1/databases/{Escape(databaseId)}",
            body: null,
            cancellationToken);
    }

    /// <summary>Gets the direct TerraBase record by database name.</summary>
    public Task<TerraScaleDatabaseRecord> GetRecordAsync(string database, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        return client.SendAsync<TerraScaleDatabaseRecord>(HttpMethod.Get, $"/api/v1/databases/{Escape(database)}", body: null, cancellationToken);
    }

    /// <summary>
    /// Deletes a TerraScale database after explicit acknowledgement of its id.
    /// </summary>
    public Task<TerraScaleDatabaseRecord> DeleteAsync(
        string databaseId,
        TerraScaleDeleteDatabaseOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(databaseId.Trim(), options.Confirmation, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The deletion confirmation must exactly match the target database id.",
                nameof(options));
        }

        return client.SendAsync<TerraScaleDatabaseRecord>(
            HttpMethod.Delete,
            $"/api/v1/databases/{Escape(databaseId)}",
            body: null,
            cancellationToken);
    }

    /// <summary>Restores a soft-deleted database within the retention window.</summary>
    public Task<TerraScaleDatabaseRecord> RestoreAsync(string database, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        return client.SendAsync<TerraScaleDatabaseRecord>(HttpMethod.Post, $"/api/v1/databases/{Escape(database)}/restore", body: null, cancellationToken);
    }

    private static string Escape(string value) =>
        Uri.EscapeDataString(value.Trim());
}

internal sealed record TerraScaleDatabaseListResponse([property: JsonRequired] IReadOnlyList<TerraScaleDatabaseRecord> Databases);
internal sealed record TerraScaleDirectCreateDatabaseRequest(
    string Name,
    IReadOnlyList<string> Placement,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Residency);

/// <summary>Direct database record published by TerraBase.</summary>
public sealed record TerraScaleDatabaseRecord(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Placement,
    [property: JsonRequired] string Residency,
    [property: JsonRequired] bool Deleted,
    [property: JsonRequired, JsonPropertyName("created_at")] long CreatedAt,
    string? Parent);

#pragma warning restore CS1591
