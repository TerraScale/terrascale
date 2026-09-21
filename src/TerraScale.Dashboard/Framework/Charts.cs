using System.Text;
using Microsoft.AspNetCore.Html;

namespace TerraScale.Dashboard;

/// <summary>Server-rendered SVG charts (sparklines + activity lines) using the
/// chart tokens; parity for the recharts islands in ts-dashboard.</summary>
public static class Charts
{
    public static HtmlString Sparkline(IReadOnlyList<double> values, string stroke = "var(--ts-chart-1)", int width = 160, int height = 32)
    {
        if (values.Count == 0)
        {
            return new HtmlString($"<svg class=\"ts-sparkline\" viewBox=\"0 0 {width} {height}\" aria-hidden=\"true\"></svg>");
        }

        var min = values.Min();
        var max = values.Max();
        var span = max - min is 0 ? 1 : max - min;
        var step = values.Count is 1 ? width : (double)width / (values.Count - 1);
        var points = values.Select((v, i) => $"{F(i * step)},{F(height - 2 - (v - min) / span * (height - 4))}");
        var line = string.Join(" L ", points);
        return new HtmlString(
            $"<svg class=\"ts-sparkline\" viewBox=\"0 0 {width} {height}\" preserveAspectRatio=\"none\" aria-hidden=\"true\">"
            + $"<path class=\"ts-sparkline-area\" d=\"M {line} L {width},{height} L 0,{height} Z\" style=\"fill:{stroke}\" />"
            + $"<path d=\"M {line}\" style=\"stroke:{stroke}\" /></svg>");
    }

    public static HtmlString LineChart(
        IReadOnlyList<WorkloadPoint> points,
        string label,
        int width = 640,
        int height = 180,
        int gridLines = 4)
    {
        if (points.Count is 0)
        {
            return new HtmlString("<div class=\"ts-muted ts-meta\">No workload data for this range.</div>");
        }

        var values = points.Select(p => p.Value).ToArray();
        var min = values.Min();
        var max = values.Max();
        var span = max - min is 0 ? 1 : max - min;
        var pad = 24;
        var plotW = width - pad * 2;
        var plotH = height - pad * 2;
        var step = points.Count is 1 ? 0 : (double)plotW / (points.Count - 1);

        var coords = points.Select((p, i) => (X: pad + i * step, Y: pad + plotH - (p.Value - min) / span * plotH)).ToArray();
        var path = "M " + string.Join(" L ", coords.Select(c => $"{F(c.X)},{F(c.Y)}"));

        var grid = new StringBuilder();
        for (var g = 0; g <= gridLines; g++)
        {
            var y = pad + (double)plotH / gridLines * g;
            var value = max - span / gridLines * g;
            grid.Append($"<line class=\"ts-chart-grid\" x1=\"{pad}\" y1=\"{F(y)}\" x2=\"{width - pad}\" y2=\"{F(y)}\" />");
            grid.Append($"<text class=\"ts-chart-axis\" x=\"4\" y=\"{F(y + 3)}\">{(int)value}</text>");
        }

        var area = $"M {pad},{pad + plotH} L " + string.Join(" L ", coords.Select(c => $"{F(c.X)},{F(c.Y)}")) + $" L {width - pad},{pad + plotH}";
        return new HtmlString($"""
            <svg class="ts-chart" viewBox="0 0 {width} {height}" role="img" aria-label="{label}">
                {grid}
                <path d="{area}" fill="var(--ts-chart-1)" opacity="0.08" />
                <path class="ts-chart-line ts-chart-series-1" d="{path}" />
            </svg>
            """);
    }

    private static string F(double value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
