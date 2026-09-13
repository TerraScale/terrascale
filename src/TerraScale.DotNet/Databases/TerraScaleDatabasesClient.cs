using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet.Generated;

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
    public Task<TerraScaleDatabaseCreationResult> CreateAsync(
        string name,
        IReadOnlyList<string> placement,
        string operationId,
        string residency,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var normalizedPlacement = placement.Select(region => TerraScaleRegionCodes.Validate(region, nameof(placement))).ToArray();
        if (normalizedPlacement.Length is < 1 or > 20)
        {
            throw new ArgumentException("Placement must contain between 1 and 20 canonical TerraScale regions.", nameof(placement));
        }
        if (residency is not ("eu-only" or "br-only"))
            throw new ArgumentException("Residency must be eu-only or br-only.", nameof(residency));
        if (residency == "eu-only" && normalizedPlacement.Any(region => region != TerraScaleRegions.Fra))
            throw new ArgumentException("eu-only residency requires FRA placement.", nameof(placement));
        if (residency == "br-only" && normalizedPlacement.Any(region => region != TerraScaleRegions.Gru))
            throw new ArgumentException("br-only residency requires GRU placement.", nameof(placement));
        return client.CreateDatabaseAsync(new CreateDatabaseRequest(Name: name.Trim(), Placement: normalizedPlacement, Residency: residency, OperationId: operationId), operationId, cancellationToken);
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

    /// <summary>Lists branches whose parent is the named database.</summary>
    public async Task<IReadOnlyList<DatabaseRecord>> ListBranchesAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        var response = await client.SendJsonAsync(
            HttpMethod.Get,
            $"/api/v1/databases/{Escape(database)}/branches",
            TerraScaleContractJsonContext.Default.BranchListResponse,
            cancellationToken).ConfigureAwait(false);
        return response.Branches;
    }

    /// <summary>Creates a branch from a named database at an optional history point.</summary>
    public Task<DatabaseRecord> CreateBranchAsync(
        string database,
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        return client.SendJsonAsync(
            HttpMethod.Post,
            $"/api/v1/databases/{Escape(database)}/branches",
            request,
            TerraScaleContractJsonContext.Default.RestoreRequest,
            TerraScaleContractJsonContext.Default.DatabaseRecord,
            cancellationToken);
    }

    private static string Escape(string value) =>
        Uri.EscapeDataString(value.Trim());
}

internal sealed record TerraScaleDatabaseListResponse([property: JsonRequired] IReadOnlyList<TerraScaleDatabaseRecord> Databases);
/// <summary>Creation can finish synchronously or accept durable provisioning work.</summary>
public abstract record TerraScaleDatabaseCreationResult;
/// <summary>A development database was created synchronously.</summary>
public sealed record TerraScaleDatabaseCreated(DatabaseRecord Database) : TerraScaleDatabaseCreationResult;
/// <summary>Durable provisioning was accepted; this does not imply serving readiness.</summary>
public sealed record TerraScaleDatabaseAccepted(ProvisioningOperation Operation) : TerraScaleDatabaseCreationResult;

/// <summary>Direct database record published by TerraBase.</summary>
public sealed record TerraScaleDatabaseRecord(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Placement,
    [property: JsonRequired] string Residency,
    [property: JsonRequired] bool Deleted,
    [property: JsonRequired, JsonPropertyName("created_at")] long CreatedAt,
    string? Parent);

#pragma warning restore CS1591
