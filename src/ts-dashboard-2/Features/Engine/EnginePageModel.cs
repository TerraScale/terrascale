namespace TerraScale.Dashboard2.Features.Engine;

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
    public ShellModel Shell { get; init; } = null!;

    public static EnginePageModel NotConfigured(ShellModel shell, string rawUrl) =>
        new(false, rawUrl, null, null, null, 0, null, null, 0, null) { Shell = shell };

    /// <summary>Loads the page: live sections when configured, setup panel otherwise.</summary>
    public static Task<EnginePageModel> LoadAsync(TerraBaseBackend backend, ShellModel shell, CancellationToken ct)
    {
#if TERRABASE_SDK
        return backend.Configured
            ? LoadLiveAsync(backend, shell, ct)
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
    private static async Task<EnginePageModel> LoadLiveAsync(TerraBaseBackend backend, ShellModel shell, CancellationToken ct)
    {
        EngineHealth? health = null;
        string? healthError = null;
        IReadOnlyList<EngineRegionRow>? regions = null;
        var hostCount = 0;
        string? catalogError = null;
        IReadOnlyList<EngineMetricInfo>? metrics = null;
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
            regions = [.. catalog.Regions
                .Select(r => new EngineRegionRow(r.Code, r.Name, r.Country, r.Latitude, r.Longitude, r.Available))
                .OrderBy(r => r.Code, StringComparer.Ordinal)];
            hostCount = catalog.Hosts.Count;
        }
        catch (Exception ex)
        {
            catalogError = ex.Message;
        }

        try
        {
            (metrics, sampleCount) = await EngineMetrics.ReadAsync(http, ct);
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
        };
    }
#endif
}
