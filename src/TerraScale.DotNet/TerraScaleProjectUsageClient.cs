namespace TerraScale.DotNet;

/// <summary>
/// Client for project usage endpoints.
/// </summary>
public sealed class TerraScaleProjectUsageClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleProjectUsageClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Gets the current usage projection for a project.
    /// </summary>
    public Task<TerraScaleProjectUsage> GetAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return client.SendAsync<TerraScaleProjectUsage>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectUsageGet(projectId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Gets the workload time-series projection for a project.
    /// </summary>
    public Task<TerraScaleProjectUsageWorkload> GetWorkloadAsync(
        string projectId,
        string? range = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return client.SendAsync<TerraScaleProjectUsageWorkload>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectUsageWorkloadGet(projectId, range),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Gets retained storage, database-operation, and egress history for a project.
    /// </summary>
    public Task<TerraScaleProjectUsageHistory> GetHistoryAsync(
        string projectId,
        string? range = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return client.SendAsync<TerraScaleProjectUsageHistory>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectUsageHistoryGet(projectId, range),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Gets geographic latency estimates for the regions configured on a project.
    /// </summary>
    public Task<TerraScaleProjectLatency> GetLatencyAsync(
        string projectId,
        double? latitude = null,
        double? longitude = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return client.SendAsync<TerraScaleProjectLatency>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectLatencyGet(projectId, latitude, longitude),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Gets organization-wide billable usage, project contributions, and plan limits.
    /// </summary>
    public Task<TerraScaleOrganizationUsage> GetOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        return client.SendAsync<TerraScaleOrganizationUsage>(
            HttpMethod.Get,
            TerraScaleApiRoutes.OrganizationUsageGet(organizationId),
            body: null,
            cancellationToken);
    }
}

/// <summary>
/// Project usage projection returned by TerraScale.
/// </summary>
public sealed record TerraScaleProjectUsage(
    string ProjectId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    IReadOnlyList<TerraScaleProjectUsageMetric> Metrics,
    IReadOnlyList<TerraScaleProjectUsageKpi> Kpis,
    IReadOnlyList<TerraScaleProjectUsageSlice> ResourceDistribution,
    IReadOnlyList<TerraScaleProjectUsageSlice> OperationBreakdown,
    IReadOnlyList<TerraScaleProjectUsageDatabaseStorage> DatabaseStorage,
    TerraScaleProjectStorageUsage Storage,
    long OperationUnits,
    long IncludedOperationUnits,
    long OverageOperationUnits,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, long> RegionalTotals,
    IReadOnlyDictionary<string, long> CategoryTotals,
    long EgressBytes = 0);

/// <summary>
/// Named project usage metric.
/// </summary>
public sealed record TerraScaleProjectUsageMetric(
    string Name,
    double Quantity,
    string Unit);

/// <summary>
/// Project usage KPI.
/// </summary>
public sealed record TerraScaleProjectUsageKpi(
    string Name,
    long Value,
    string FormattedValue,
    string Detail,
    string Status);

/// <summary>
/// Named project usage quantity.
/// </summary>
public sealed record TerraScaleProjectUsageSlice(
    string Name,
    long Quantity);

/// <summary>
/// Project storage usage summary.
/// </summary>
public sealed record TerraScaleProjectStorageUsage(
    long UsedBytes,
    long LimitBytes,
    int PercentUsed,
    string Status);

/// <summary>
/// Per-database project storage usage summary.
/// </summary>
public sealed record TerraScaleProjectUsageDatabaseStorage(
    string DatabaseId,
    string Name,
    long Documents,
    long StorageBytes);

/// <summary>
/// Project workload time-series projection returned by TerraScale.
/// </summary>
public sealed record TerraScaleProjectUsageWorkload(
    string ProjectId,
    string Range,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string BucketSize,
    IReadOnlyList<TerraScaleProjectUsageWorkloadPoint> Points);

/// <summary>
/// One project workload time bucket.
/// </summary>
public sealed record TerraScaleProjectUsageWorkloadPoint(
    DateTimeOffset Start,
    DateTimeOffset End,
    string Label,
    int Value);

/// <summary>
/// Retained project usage history and its accessible retention window.
/// </summary>
public sealed record TerraScaleProjectUsageHistory(
    string ProjectId,
    string Range,
    DateTimeOffset RequestedStart,
    DateTimeOffset AccessibleStart,
    DateTimeOffset End,
    bool LimitedByRetention,
    IReadOnlyList<TerraScaleProjectUsageHistoryPoint> Points);

/// <summary>
/// One retained project usage observation.
/// </summary>
public sealed record TerraScaleProjectUsageHistoryPoint(
    DateTimeOffset ObservedAt,
    long StorageBytes,
    long OperationUnits,
    long EgressBytes);

/// <summary>
/// Geographic latency estimates for configured project regions.
/// </summary>
public sealed record TerraScaleProjectLatency(
    string ProjectId,
    DateTimeOffset CapturedAt,
    string Source,
    IReadOnlyList<TerraScaleProjectRegionLatency> InterRegion,
    TerraScaleProjectNearestRegionLatency? NearestRegion);

/// <summary>
/// Estimated round-trip latency between two configured project regions.
/// </summary>
public sealed record TerraScaleProjectRegionLatency(
    string FromRegion,
    string ToRegion,
    double EstimatedRoundTripMilliseconds);

/// <summary>
/// Estimated round-trip latency from the caller's location to the nearest project region.
/// </summary>
public sealed record TerraScaleProjectNearestRegionLatency(
    string Region,
    double EstimatedRoundTripMilliseconds);

/// <summary>
/// Consolidated billable organization usage and target-plan status.
/// </summary>
public sealed record TerraScaleOrganizationUsage(
    string OrganizationId,
    string PlanId,
    string PlanName,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset UpdatedAt,
    bool Provisional,
    IReadOnlyList<TerraScaleOrganizationUsageLimit> Resources,
    IReadOnlyList<TerraScaleOrganizationProjectUsage> Projects);

/// <summary>
/// One billable resource measured against the organization's plan.
/// </summary>
public sealed record TerraScaleOrganizationUsageLimit(
    string Resource,
    string Unit,
    long Used,
    long? Included,
    long? Remaining,
    long? Excess,
    string Status,
    bool Provisional);

/// <summary>
/// One project's contribution to organization-wide billable usage.
/// </summary>
public sealed record TerraScaleOrganizationProjectUsage(
    string ProjectId,
    string ProjectName,
    long? StorageBytes,
    long? OperationUnits,
    long? EgressBytes,
    DateTimeOffset? UpdatedAt,
    bool Provisional);
