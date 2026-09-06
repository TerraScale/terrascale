namespace TerraScale.DotNet;

/// <summary>
/// Client for physical database branch lifecycle endpoints.
/// </summary>
public sealed class TerraScaleDatabaseBranchesClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleDatabaseBranchesClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Gets the physical database branch lifecycle capability for a project.
    /// </summary>
    public Task<TerraScaleDatabaseBranchCapability> GetCapabilityAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return client.SendDashboardAsync<TerraScaleDatabaseBranchCapability>(
            HttpMethod.Get,
            TerraScaleApiRoutes.DatabaseBranchesCapabilityGet(projectId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Requests a physical clone of a project database.
    /// </summary>
    /// <remarks>
    /// Platform currently fails closed with a <see cref="TerraScaleApiException"/> using
    /// <c>database_branch_unavailable</c> until an Orchestrator-backed physical lifecycle controller is available.
    /// </remarks>
    public async Task RequestCloneAsync(
        string projectId,
        TerraScaleCreateDatabaseBranchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceDatabaseId);

        _ = await client.SendDashboardAsync<TerraScaleDatabaseBranchCapability>(
            HttpMethod.Post,
            TerraScaleApiRoutes.DatabaseBranchesCreate(projectId),
            request,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Request body for a physical database branch clone.
/// </summary>
public sealed record TerraScaleCreateDatabaseBranchRequest(string SourceDatabaseId);

/// <summary>
/// Physical database branch lifecycle admission for a project.
/// </summary>
public sealed record TerraScaleDatabaseBranchCapability(
    string ProjectId,
    string AdmissionState,
    bool PhysicalCloneAvailable,
    bool PromotionAvailable,
    bool MergeAvailable,
    string Detail);
