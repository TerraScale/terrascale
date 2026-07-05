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
    TerraScaleProjectStorageUsage Storage);

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
