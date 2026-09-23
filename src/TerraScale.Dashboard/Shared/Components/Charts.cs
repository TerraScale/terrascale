using System.Text;
using Microsoft.AspNetCore.Html;

namespace TerraScale.Dashboard.Shared.Components;

/// <summary>Server-rendered SVG charts (sparklines + activity lines) using the
/// chart tokens; parity for the recharts islands in ts-dashboard.</summary>
public sealed record ChartSeries(string Name, IReadOnlyList<double> Values);

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

    /// <summary>Zero-based multi-line chart on one y-axis. Series take the
    /// categorical slots in order (ts-cat-1, ts-cat-2, ...); hovering a column
    /// shows the exact timestamp and every series' value.</summary>
    public static HtmlString TimeSeriesChart(
        IReadOnlyList<DateTime> times,
        IReadOnlyList<ChartSeries> series,
        string label,
        string tickFormat,
        int width = 720,
        int height = 220)
    {
        if (times.Count is 0 || series.Count is 0)
        {
            return new HtmlString("");
        }

        const int left = 44, right = 8, top = 12, bottom = 28, gridLines = 4;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var plotW = width - left - right;
        var plotH = height - top - bottom;
        var max = NiceCeiling(series.Max(s => s.Values.Max()));
        var step = times.Count is 1 ? 0 : (double)plotW / (times.Count - 1);
        double X(int i) => left + i * step;
        double Y(double v) => top + plotH - v / max * plotH;

        var html = new StringBuilder("<div class=\"ts-tschart\">");
        html.Append($"""<svg class="ts-chart" viewBox="0 0 {width} {height}" role="img" aria-label="{label}">""");
        for (var g = 0; g <= gridLines; g++)
        {
            var y = top + (double)plotH / gridLines * g;
            html.Append($"""<line class="ts-chart-grid" x1="{left}" y1="{F(y)}" x2="{width - right}" y2="{F(y)}" />""");
            html.Append($"""<text class="ts-chart-axis" x="{left - 8}" y="{F(y + 3)}" text-anchor="end">{Compact(max - max / gridLines * g)}</text>""");
        }

        var last = times.Count - 1;
        foreach (var i in Enumerable.Range(0, 5).Select(k => (int)Math.Round(k * last / 4.0)).Distinct())
        {
            var anchor = i == 0 ? "start" : i == last ? "end" : "middle";
            html.Append($"""<text class="ts-chart-axis" x="{F(X(i))}" y="{height - 8}" text-anchor="{anchor}">{times[i].ToString(tickFormat, inv)}</text>""");
        }

        for (var k = 0; k < series.Count; k++)
        {
            var line = string.Join(" L ", series[k].Values.Select((v, i) => $"{F(X(i))},{F(Y(v))}"));
            html.Append($"""<path class="ts-chart-line ts-cat-{k + 1}" d="M {line}" />""");
        }

        html.Append("</svg>");

        // ponytail: one hover column per point (CSS-only tooltip); fine for a few hundred points.
        // The overlay is aria-hidden; the legend totals carry the text summary.
        var hitW = times.Count is 1 ? plotW : step;
        html.Append("""<div class="ts-tschart-hover" aria-hidden="true">""");
        for (var i = 0; i < times.Count; i++)
        {
            var flip = X(i) > width / 2.0 ? " ts-tschart-tip-left" : "";
            html.Append($"""<div class="ts-tschart-col" style="left:{P(X(i) - hitW / 2, width)}%;width:{P(hitW, width)}%;top:{P(top, height)}%;height:{P(plotH, height)}%">""");
            for (var k = 0; k < series.Count; k++)
            {
                html.Append($"""<span class="ts-tschart-dot ts-cat-{k + 1}" style="top:{P(Y(series[k].Values[i]) - top, plotH)}%"></span>""");
            }

            html.Append($"""<div class="ts-tschart-tip{flip}"><time datetime="{times[i]:yyyy-MM-ddTHH:mmZ}">{times[i].ToString("ddd, MMM d, yyyy · HH:mm 'UTC'", inv)}</time><dl>""");
            for (var k = 0; k < series.Count; k++)
            {
                html.Append($"""<div><dt><span class="ts-chart-swatch ts-cat-{k + 1}"></span>{series[k].Name}</dt><dd>{series[k].Values[i].ToString("N0", inv)}</dd></div>""");
            }

            html.Append("</dl></div></div>");
        }

        html.Append("</div></div>");
        return new HtmlString(html.ToString());
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

    private static string P(double part, double whole) => (part / whole * 100).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string F(double value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
