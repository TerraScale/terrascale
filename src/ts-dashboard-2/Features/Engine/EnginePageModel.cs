using TerraScale.DotNet;

namespace TerraScale.Dashboard2.Features.Engine;

public sealed record EnginePageModel(
    bool Configured,
    string Url,
    TerraScaleRegionHealth? Health,
    string? HealthError,
    IReadOnlyList<TerraScaleRegion>? Regions,
    int HostCount,
    string? CatalogError,
    IReadOnlyList<EngineMetricInfo>? Metrics,
    int SampleCount,
    string? MetricsError) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static EnginePageModel NotConfigured(ShellModel shell, string rawUrl) =>
        new(false, rawUrl, null, null, null, 0, null, null, 0, null) { Shell = shell };

    /// <summary>
    /// Fetches live member health, the region catalog, and the metric surface.
    /// Each section fails independently so one dead endpoint renders an error
    /// panel instead of killing the page.
    /// </summary>
    public static async Task<EnginePageModel> LoadLiveAsync(TerraBaseBackend backend, ShellModel shell, CancellationToken ct)
    {
        TerraScaleRegionHealth? health = null;
        string? healthError = null;
        IReadOnlyList<TerraScaleRegion>? regions = null;
        var hostCount = 0;
        string? catalogError = null;
        IReadOnlyList<EngineMetricInfo>? metrics = null;
        var sampleCount = 0;
        string? metricsError = null;

        using var http = backend.CreateHttpClient();
        var client = backend.CreateClient(http);
        try
        {
            health = await client.Regions.HealthAsync(ct);
        }
        catch (Exception ex)
        {
            healthError = ex.Message;
        }

        try
        {
            var catalog = await client.Regions.GetAsync(ct);
            regions = [.. catalog.Regions.OrderBy(r => r.Code, StringComparer.Ordinal)];
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
}
