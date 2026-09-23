using System.Text;
using Microsoft.AspNetCore.Html;

namespace TerraScale.Dashboard.Shared.Components;

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

    /// <summary>Circular meter for one allowance. <paramref name="tone"/> is a
    /// status tone (success | warning | danger); overage clamps to a full ring.</summary>
    public static HtmlString Ring(double percent, string tone, string label)
    {
        var fill = Math.Clamp(percent, 0, 100);
        return new HtmlString($"""
            <svg class="ts-ring ts-ring-{tone}" viewBox="0 0 120 120" role="img" aria-label="{label}">
                <circle class="ts-ring-track" cx="60" cy="60" r="52" pathLength="100" />
                <circle class="ts-ring-fill" cx="60" cy="60" r="52" pathLength="100" stroke-dasharray="{F(fill)} 100" />
            </svg>
            """);
    }

    /// <summary>Zero-based area chart with a y-axis, first/middle/last x ticks and
    /// a per-point hover crosshair whose native tooltip reads "value · label".</summary>
    public static HtmlString AreaChart(IReadOnlyList<WorkloadPoint> points, string label, int width = 720, int height = 220)
    {
        if (points.Count is 0)
        {
            return new HtmlString("");
        }

        const int left = 44, right = 8, top = 12, bottom = 28, gridLines = 4;
        var plotW = width - left - right;
        var plotH = height - top - bottom;
        var max = NiceCeiling(points.Max(p => p.Value));
        var step = points.Count is 1 ? 0 : (double)plotW / (points.Count - 1);
        var coords = points.Select((p, i) => (X: left + i * step, Y: top + plotH - p.Value / max * plotH)).ToArray();
        var line = string.Join(" L ", coords.Select(c => $"{F(c.X)},{F(c.Y)}"));

        var svg = new StringBuilder();
        svg.Append($"""<svg class="ts-chart ts-area" viewBox="0 0 {width} {height}" role="img" aria-label="{label}">""");
        svg.Append("""<defs><linearGradient id="ts-area-fill" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="var(--ts-chart-1)" stop-opacity="0.35" /><stop offset="1" stop-color="var(--ts-chart-1)" stop-opacity="0" /></linearGradient></defs>""");
        for (var g = 0; g <= gridLines; g++)
        {
            var y = top + (double)plotH / gridLines * g;
            svg.Append($"""<line class="ts-chart-grid" x1="{left}" y1="{F(y)}" x2="{width - right}" y2="{F(y)}" />""");
            svg.Append($"""<text class="ts-chart-axis" x="{left - 8}" y="{F(y + 3)}" text-anchor="end">{Compact(max - max / gridLines * g)}</text>""");
        }

        foreach (var (index, anchor) in new[] { (0, "start"), (points.Count / 2, "middle"), (points.Count - 1, "end") }.DistinctBy(t => t.Item1))
        {
            svg.Append($"""<text class="ts-chart-axis" x="{F(coords[index].X)}" y="{height - 8}" text-anchor="{anchor}">{points[index].Label}</text>""");
        }

        svg.Append($"""<path d="M {left},{top + plotH} L {line} L {F(coords[^1].X)},{top + plotH} Z" fill="url(#ts-area-fill)" />""");
        svg.Append($"""<path class="ts-chart-line ts-chart-series-1" d="M {line}" />""");

        // ponytail: native <title> tooltips; swap for an HTML tooltip island if richer hover is needed.
        var hitW = points.Count is 1 ? plotW : step;
        for (var i = 0; i < coords.Length; i++)
        {
            var (x, y) = coords[i];
            svg.Append($"""<g class="ts-area-point"><rect x="{F(x - hitW / 2)}" y="{top}" width="{F(hitW)}" height="{plotH}" /><line x1="{F(x)}" y1="{top}" x2="{F(x)}" y2="{top + plotH}" /><circle cx="{F(x)}" cy="{F(y)}" r="4" /><title>{points[i].Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} · {points[i].Label}</title></g>""");
        }

        svg.Append("</svg>");
        return new HtmlString(svg.ToString());
    }

    // 1-2-5 rounding so the y-axis lands on readable ticks.
    private static double NiceCeiling(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        return new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * magnitude).First(v => v >= value);
    }

    private static string Compact(double value) => value switch
    {
        >= 1_000_000 => $"{F(value / 1_000_000)}M",
        >= 1_000 => $"{F(value / 1_000)}k",
        _ => F(value),
    };

    private static string F(double value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
