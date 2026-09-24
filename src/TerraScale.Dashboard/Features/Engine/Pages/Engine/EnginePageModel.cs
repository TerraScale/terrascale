namespace TerraScale.Dashboard.Features.Engine.Pages.Engine;

public sealed record EnginePageModel(
    bool Configured,
    string Url,
    EngineHealth? Health,
    string? HealthError,
    IReadOnlyList<EngineRegionRow>? Regions,
    int HostCount,
    string? CatalogError,
    IReadOnlyList<EngineMetricInfo>? Metrics,
    int SampleCount,
    string? MetricsError) : IPageModel
{
    public const int PageSize = 10;

    public ShellModel Shell { get; init; } = null!;

    public int RegionsPage { get; init; } = 1;
    public int RegionsPageCount { get; init; } = 1;
    public int RegionsTotal { get; init; }
    public int RegionsFirst => RegionsTotal is 0 ? 0 : ((RegionsPage - 1) * PageSize) + 1;
    public int RegionsLast => Math.Min(RegionsPage * PageSize, RegionsTotal);

    public int MetricsPage { get; init; } = 1;
    public int MetricsPageCount { get; init; } = 1;
    public int MetricsTotal { get; init; }
    public int MetricsFirst => MetricsTotal is 0 ? 0 : ((MetricsPage - 1) * PageSize) + 1;
    public int MetricsLast => Math.Min(MetricsPage * PageSize, MetricsTotal);

    public static EnginePageModel NotConfigured(ShellModel shell, string rawUrl) =>
        new(false, rawUrl, null, null, null, 0, null, null, 0, null) { Shell = shell };

    /// <summary>Loads the page: live sections when configured, setup panel otherwise.</summary>
    public static Task<EnginePageModel> LoadAsync(TerraBaseBackend backend, ShellModel shell, CancellationToken ct,
        int regionsPage = 1, int metricsPage = 1)
    {
#if TERRABASE_SDK
        return backend.Configured
            ? LoadLiveAsync(backend, shell, ct, regionsPage, metricsPage)
            : Task.FromResult(NotConfigured(shell, backend.RawUrl));
#else
        return Task.FromResult(NotConfigured(shell, backend.RawUrl));
#endif
    }

#if TERRABASE_SDK
    /// <summary>
    /// Fetches live member health, the region catalog, and the metric surface.
    /// Each section fails independently so one dead endpoint renders an error
    /// panel instead of killing the page.
    /// </summary>
    private static async Task<EnginePageModel> LoadLiveAsync(TerraBaseBackend backend, ShellModel shell, CancellationToken ct,
        int regionsPage, int metricsPage)
    {
        EngineHealth? health = null;
        string? healthError = null;
        IReadOnlyList<EngineRegionRow>? regions = null;
        var regionsTotal = 0;
        var currentRegionsPage = 1;
        var regionsPageCount = 1;
        var hostCount = 0;
        string? catalogError = null;
        IReadOnlyList<EngineMetricInfo>? metrics = null;
        var metricsTotal = 0;
        var currentMetricsPage = 1;
        var metricsPageCount = 1;
        var sampleCount = 0;
        string? metricsError = null;

        using var http = backend.CreateHttpClient();
        var client = backend.CreateClient(http);
        try
        {
            var report = await client.Regions.HealthAsync(ct);
            health = new EngineHealth(report.Region, report.Version, report.Healthy);
        }
        catch (Exception ex)
        {
            healthError = ex.Message;
        }

        try
        {
            var catalog = await client.Regions.GetAsync(ct);
            var allRegions = catalog.Regions
                .Select(r => new EngineRegionRow(r.Code, r.Name, r.Country, r.Latitude, r.Longitude, r.Available))
                .OrderBy(r => r.Code, StringComparer.Ordinal)
                .ToList();
            regionsTotal = allRegions.Count;
            regionsPageCount = Math.Max(1, (regionsTotal + PageSize - 1) / PageSize);
            currentRegionsPage = Math.Clamp(regionsPage, 1, regionsPageCount);
            regions = [.. allRegions.Skip((currentRegionsPage - 1) * PageSize).Take(PageSize)];
            hostCount = catalog.Hosts.Count;
        }
        catch (Exception ex)
        {
            catalogError = ex.Message;
        }

        try
        {
            var result = await EngineMetrics.ReadAsync(http, ct);
            var allMetrics = result.Metrics.ToList();
            metricsTotal = allMetrics.Count;
            metricsPageCount = Math.Max(1, (metricsTotal + PageSize - 1) / PageSize);
            currentMetricsPage = Math.Clamp(metricsPage, 1, metricsPageCount);
            metrics = [.. allMetrics.Skip((currentMetricsPage - 1) * PageSize).Take(PageSize)];
            sampleCount = result.Samples;
        }
        catch (Exception ex)
        {
            metricsError = ex.Message;
        }

        return new EnginePageModel(
            true,
            backend.Url!.ToString(),
            health,
            healthError,
            regions,
            hostCount,
            catalogError,
            metrics,
            sampleCount,
            metricsError)
        {
            Shell = shell,
            RegionsPage = currentRegionsPage,
            RegionsPageCount = regionsPageCount,
            RegionsTotal = regionsTotal,
            MetricsPage = currentMetricsPage,
            MetricsPageCount = metricsPageCount,
            MetricsTotal = metricsTotal,
        };
    }
#endif

    public string RegionsHref(int page)
    {
        var query = new List<string>();
        if (page > 1) query.Add($"regionsPage={page}");
        if (MetricsPage > 1) query.Add($"metricsPage={MetricsPage}");
        return query.Count is 0 ? "/engine" : "/engine?" + string.Join('&', query);
    }

    public string MetricsHref(int page)
    {
        var query = new List<string>();
        if (RegionsPage > 1) query.Add($"regionsPage={RegionsPage}");
        if (page > 1) query.Add($"metricsPage={page}");
        return query.Count is 0 ? "/engine" : "/engine?" + string.Join('&', query);
    }
}
