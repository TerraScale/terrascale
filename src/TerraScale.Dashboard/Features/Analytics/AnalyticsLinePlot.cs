using System.Globalization;
using System.Text;
using TerraScale.Dashboard.Shared.Charts;

namespace TerraScale.Dashboard.Features.Analytics;

public sealed record AnalyticsSeries(string Name, long[] Values, bool[]? Missing = null);
public sealed record AnalyticsLinePlot(string Label, string Area, IReadOnlyList<ChartGrid> Grid, IReadOnlyList<ChartTick> Ticks,
    IReadOnlyList<RequestPath> Paths, IReadOnlyList<RequestDot> Markers, IReadOnlyList<RequestColumn> Columns)
{
    public static AnalyticsLinePlot For(string label, string range, IReadOnlyList<DateTimeOffset> starts, IReadOnlyList<AnalyticsSeries> series)
    {
        if (starts.Count == 0 || series.Count == 0 || series.Any(row => row.Values.Length != starts.Count || row.Missing is { } missing && missing.Length != starts.Count))
            return new(label, "", [], [], [], [], []);
        bool Missing(AnalyticsSeries row, int index) => row.Missing?[index] == true;
        var maximum = series.SelectMany(row => row.Values.Where((value, index) => !Missing(row, index))).DefaultIfEmpty().Max();
        // Four whole, round grid steps (1, 2 or 5 × 10ⁿ each): 40 → 0, 10, 20, 30, 40 rather than 0, 12, 25, 37, 50.
        var gridStep = Enumerable.Range(0, 20).SelectMany(power => new[] { 1d, 2d, 5d }.Select(factor => factor * Math.Pow(10, power))).First(value => value * 4 >= maximum);
        var scale = maximum == 0 ? 1 : gridStep * 4;
        double X(int index) => 48 + 660d * index / Math.Max(1, starts.Count - 1);
        double Y(long value) => 14 + 154 * (1 - value / scale);
        string C(double value) => ChartGeometry.Coordinate(value);
        var grid = Enumerable.Range(0, 5).Select(step => new ChartGrid(C(14 + 154d * step / 4), maximum > 0 ? Axis(Math.Floor(scale * (4 - step) / 4)) : step == 4 ? "0" : "")).ToArray();
        var seen = new HashSet<int>();
        var ticks = Enumerable.Range(0, 5).Select(step =>
        {
            var index = (int)Math.Round(step * (starts.Count - 1) / 4d, MidpointRounding.AwayFromZero);
            var visible = (starts.Count == 1 || index != starts.Count - 1 || step == 4) && seen.Add(index);
            return new ChartTick(C(X(index)), visible ? starts[index].UtcDateTime.ToString(range is "7d" or "30d" ? "MMM d HH:mm" : "HH:mm", CultureInfo.InvariantCulture) : "", step == 0 ? "start" : step == 4 ? "end" : "middle");
        }).ToArray();
        var paths = new List<RequestPath>(); var markers = new List<RequestDot>();
        foreach (var (row, index) in series.Select((row, index) => (row, index)))
        {
            var color = series.Count == 1 ? "ts-chart-series-1" : "ts-cat-" + (index % 3 + 1);
            var dash = index < 3 ? "" : 3 * (index / 3 + 1) + " 5";
            var segments = new List<string>(); var segment = new List<(double X, double Y)>();
            for (var sample = 0; sample < starts.Count; sample++)
            {
                if (Missing(row, sample)) { Flush(); continue; }
                segment.Add((X(sample), Y(row.Values[sample])));
                if ((sample == 0 || Missing(row, sample - 1)) && (sample == starts.Count - 1 || Missing(row, sample + 1))) markers.Add(new(C(X(sample)), C(Y(row.Values[sample])), color, dash));
            }
            Flush(); paths.Add(new(row.Name, color, dash, string.Join(' ', segments)));
            void Flush() { if (segment.Count == 0) return; segments.Add(ChartGeometry.Smooth(segment.ToArray())); segment.Clear(); }
        }
        var columns = new List<RequestColumn>();
        var columnWidth = 660d / Math.Max(1, starts.Count - 1);
        for (var index = 0; index < starts.Count; index++)
        {
            var header = starts[index].UtcDateTime.ToString("MMM d HH:mm 'UTC'", CultureInfo.InvariantCulture);
            var rows = paths.Select((path, order) => new RequestTipRow(path.Name, Missing(series[order], index) ? "Not reported" : series[order].Values[index].ToString("N0", CultureInfo.InvariantCulture), path.Class, path.Dash, C(14 + 6 + 17 * (order + 1.5)))).ToArray();
            var width = Math.Max(header.EnumerateRunes().Count() * 6.8, 32 + (rows.Max(row => row.Name.EnumerateRunes().Count()) + rows.Max(row => row.Value.EnumerateRunes().Count())) * 6.8 + 16) + 20;
            var left = X(index) + 10 + width > 708 ? Math.Max(0, X(index) - 10 - width) : X(index) + 10;
            var dots = paths.Select((path, order) => (path, order)).Where(value => !Missing(series[value.order], index)).Select(value => new RequestDot(C(X(index)), C(Y(series[value.order].Values[index])), value.path.Class, value.path.Dash)).ToArray();
            var bandLeft = Math.Round(index == 0 ? 48 : X(index) - columnWidth / 2, 1);
            var bandRight = Math.Round(index == starts.Count - 1 ? 708 : X(index) + columnWidth / 2, 1);
            columns.Add(new(C(X(index)), C(bandLeft), C(bandRight - bandLeft), header + ": " + string.Join(", ", rows.Select(row => row.Name + " " + row.Value)), header, C(left), C(width), C(12 + (rows.Length + 1) * 17), dots, rows));
        }
        return new(label, series.Count == 1 && !(series[0].Missing?.Contains(true) ?? false) ? paths[0].Path + " L 708,168 L 48,168 Z" : "", grid, ticks, paths, markers, columns);
    }

    // Axis labels fit the 48-unit gutter: "750k", not "750.0k".
    private static string Axis(double value) => value >= 1000000 ? (value / 1000000).ToString("0.#", CultureInfo.InvariantCulture) + "M" :
        value >= 1000 ? (value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : value.ToString(CultureInfo.InvariantCulture);
}

// Shared by the three chart slices. Only the displayed tip is cloned into the final SVG layer;
// chronological hit columns and their accessible names never move.
public static class ChartInteraction
{
    public const string Initialize = """
        const columns = [...el.querySelectorAll('.ts-chart-column')], layer = el.querySelector('.ts-chart-overlay');
        columns.forEach((column, i) => { column.tabIndex = i === columns.length - 1 ? 0 : -1; column.dataset.chartBucket = i; column.toggleAttribute('data-chart-latest', i === columns.length - 1); });
        let active = null, pointer = false, position = null, dismissed = false;
        const show = column => {
          if (active !== column || !layer.firstChild) {
            active?.classList.remove('ts-chart-active'); active = column; layer.replaceChildren();
            if (column) { column.classList.add('ts-chart-active'); layer.append(column.querySelector('.ts-chart-tip').cloneNode(true)); layer.dataset.chartBucket = column.dataset.chartBucket; }
          }
          layer.classList.toggle('is-active', !!column && !dismissed);
          if (!column || dismissed) return;
          layer.querySelector('.ts-chart-tip-bridge')?.remove();
          const box = layer.querySelector('.ts-chart-tip-box').getBoundingClientRect(), hit = column.querySelector('.ts-chart-hit').getBoundingClientRect();
          const x = (hit.left + hit.right) / 2, inverse = el.getScreenCTM().inverse();
          const a = new DOMPoint(Math.min(x, box.left), box.top).matrixTransform(inverse), b = new DOMPoint(Math.max(x, box.right), box.bottom).matrixTransform(inverse);
          const bridge = document.createElementNS(el.namespaceURI, 'rect'); bridge.setAttribute('class', 'ts-chart-tip-bridge');
          for (const [name, value] of Object.entries({x: a.x, y: a.y, width: b.x - a.x, height: b.y - a.y})) bridge.setAttribute(name, value);
          layer.prepend(bridge);
        };
        // Keep the selection while crossing other bands toward the tip, including a diagonal
        // approach from below it. Horizontal movement below the tip still selects other buckets.
        const towards = (from, to, box) => {
          if (from.x === to.x && from.y === to.y) return false;
          let low = 0, high = Infinity;
          for (const [p, d, min, max] of [[from.x, to.x - from.x, box.left, box.right], [from.y, to.y - from.y, box.top, box.bottom]]) {
            if (!d) { if (p < min || p > max) return false; continue; }
            const a = (min - p) / d, b = (max - p) / d;
            low = Math.max(low, Math.min(a, b)); high = Math.min(high, Math.max(a, b));
          }
          return high >= low && high > 0;
        };
        el.chartPointer = evt => {
          pointer = true; const previous = position; position = {x: evt.clientX, y: evt.clientY};
          if (evt.target.closest('.ts-chart-overlay')) return;
          const column = evt.target.closest('.ts-chart-column'); if (!column || column === active) return;
          if (active && !dismissed && previous && towards(previous, position, layer.querySelector('.ts-chart-tip-box').getBoundingClientRect())) return;
          dismissed = false; show(column);
        };
        el.chartFocus = evt => { const column = evt.target.closest('.ts-chart-column'); if (column) { dismissed = false; position = null; show(column); } };
        el.chartLeave = () => { pointer = false; position = null; show(el.querySelector('.ts-chart-column:focus-visible')); };
        el.chartBlur = () => { if (!pointer) show(null); };
        el.chartResize = () => show(active);
        el.chartDismiss = () => { dismissed = true; layer.classList.remove('is-active'); };
        el.chartKey = evt => {
          const index = columns.indexOf(evt.target); if (index < 0 || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(evt.key)) return;
          evt.preventDefault(); const next = columns[Math.max(0, Math.min(columns.length - 1, {ArrowLeft: index - 1, ArrowRight: index + 1, Home: 0, End: columns.length - 1}[evt.key]))];
          if (next !== evt.target) { evt.target.tabIndex = -1; next.tabIndex = 0; next.focus(); }
        };
        """;
}
