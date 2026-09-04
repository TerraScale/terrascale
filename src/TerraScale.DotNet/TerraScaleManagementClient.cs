namespace TerraScale.DotNet;

/// <summary>
/// Named management operations exposed by the TerraBase public API.
/// </summary>
/// <remarks>
/// The management surface is deliberately composed from the named clients so
/// every operation continues to use the shared cancellation, retry,
/// idempotency, pagination, routing, and typed-error handling.  An operation
/// is not added here until its route is present in the pinned TerraBase
/// contract.
/// </remarks>
public sealed class TerraScaleManagementClient
{
    /// <summary>
    /// Creates a management surface over a caller-owned TerraScale client.
    /// </summary>
    public TerraScaleManagementClient(TerraScaleClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        Databases = client.Databases;
        Branches = client.DatabaseBranches;
        Collections = client.Collections;
        Schemas = client.Schemas;
        Backups = client.Backups;
        ApiKeys = client.ApiKeys;
        Usage = new TerraScaleUsageClient(client);
    }

    /// <summary>
    /// Database create, list, get, and delete operations.
    /// </summary>
    public TerraScaleDatabasesClient Databases { get; }

    /// <summary>
    /// Database branch create and list operations.
    /// </summary>
    public TerraScaleDatabaseBranchesClient Branches { get; }

    /// <summary>
    /// Database branch operations; this explicit name mirrors the root client property.
    /// </summary>
    public TerraScaleDatabaseBranchesClient DatabaseBranches => Branches;

    /// <summary>
    /// Database collection list operations.
    /// </summary>
    public TerraScaleCollectionsClient Collections { get; }

    /// <summary>
    /// Collection schema operations.
    /// </summary>
    public TerraScaleSchemasClient Schemas { get; }

    /// <summary>
    /// Backup, point-in-time restore, and restore-job operations.
    /// </summary>
    public TerraScaleBackupsClient Backups { get; }

    /// <summary>
    /// Project API-key operations from the maintained project contract.
    /// </summary>
    public TerraScaleApiKeysClient ApiKeys { get; }

    /// <summary>
    /// Database usage operations from the TerraBase contract.
    /// </summary>
    public TerraScaleUsageClient Usage { get; }
}

/// <summary>
/// Named database-usage operations.
/// </summary>
public sealed class TerraScaleUsageClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleUsageClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Gets usage facts for one database.
    /// </summary>
    /// <param name="databaseId">The TerraBase database name.</param>
    /// <param name="cancellationToken">Token used to cancel the request and response read.</param>
    public Task<TerraScaleDatabaseUsage> GetAsync(
        string databaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        return client.SendAsync<TerraScaleDatabaseUsage>(
            HttpMethod.Get,
            $"/api/v1/databases/{Uri.EscapeDataString(databaseId.Trim())}/usage",
            body: null,
            cancellationToken);
    }
}

/// <summary>
/// Usage facts returned for a TerraBase database.
/// </summary>
public sealed record TerraScaleDatabaseUsage(
    long Writes,
    long StorageBytes);
