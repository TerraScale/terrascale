namespace TerraScale.Dashboard2.Features.Engine;

/// <summary>
/// Reads the raw Prometheus text at /internal/metrics and keeps only the HELP
/// lines (metric name + help) plus the sample count — never the sample
/// payloads, which can be hundreds of kilobytes per scrape.
/// </summary>
public static class EngineMetrics
{
    private const string HelpPrefix = "# HELP ";

    public static async Task<(IReadOnlyList<EngineMetricInfo> Metrics, int Samples)> ReadAsync(HttpClient http, CancellationToken ct)
    {
        var text = await http.GetStringAsync("/internal/metrics", ct);
        List<EngineMetricInfo> metrics = [];
        var samples = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.StartsWith(HelpPrefix, StringComparison.Ordinal))
            {
                var rest = line[HelpPrefix.Length..];
                var split = rest.IndexOf(' ');
                if (split > 0)
                {
                    metrics.Add(new EngineMetricInfo(rest[..split], rest[(split + 1)..].Trim()));
                }
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                samples++;
            }
        }

        return (metrics, samples);
    }
}
