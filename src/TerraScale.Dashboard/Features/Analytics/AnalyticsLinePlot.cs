using System.Globalization;
using TerraScale.Dashboard.Common.Charts;
using TerraScale.Dashboard.Common.Formatting;

namespace TerraScale.Dashboard.Features.Analytics
{
    // Decimal preserves exact 64-bit counters alongside fractional millisecond observations.
    public sealed record AnalyticsSeries(string Name, decimal[] Values, bool[]? Missing = null);
    public sealed record AnalyticsLinePlot(string Label, string Area, IReadOnlyList<ChartGrid> Grid, IReadOnlyList<ChartTick> Ticks,
        IReadOnlyList<RequestPath> Paths, IReadOnlyList<RequestDot> Markers, IReadOnlyList<RequestColumn> Columns)
    {
        public static AnalyticsLinePlot For(string label, string range, IReadOnlyList<DateTimeOffset> starts, IReadOnlyList<AnalyticsSeries> series)
        {
            return Build(label, range, starts, series, value => value.ToString("N0", CultureInfo.InvariantCulture), value => ChartGeometry.Compact(Math.Floor(value)), 0);
        }

        public static AnalyticsLinePlot ForLatency(string label, string range, IReadOnlyList<DateTimeOffset> starts, IReadOnlyList<AnalyticsSeries> series)
        {
            return Build(label, range, starts, series, value => MetricFormat.Latency((double)value), value => MetricFormat.Latency(value), -6);
        }

        private static AnalyticsLinePlot Build(string label, string range, IReadOnlyList<DateTimeOffset> starts, IReadOnlyList<AnalyticsSeries> series,
            Func<decimal, string> format, Func<double, string> axis, int firstPower)
        {
            if (starts.Count == 0 || series.Count == 0 || series.Any(row => row.Values.Length != starts.Count || row.Missing is { } missing && missing.Length != starts.Count))
            {
                return new(label, "", [], [], [], [], []);
            }

            bool Missing(AnalyticsSeries row, int index) => row.Missing?[index] == true;
            var maximum = series.SelectMany(row => row.Values.Where((value, index) => !Missing(row, index))).DefaultIfEmpty().Max();
            // Four whole, round grid steps (1, 2 or 5 × 10ⁿ each): 40 → 0, 10, 20, 30, 40 rather than 0, 12, 25, 37, 50.
            var gridStep = Enumerable.Range(firstPower, 20 - firstPower).SelectMany(power => new[] { 1d, 2d, 5d }.Select(factor => factor * Math.Pow(10, power))).First(value => value * 4 >= (double)maximum);
            var scale = maximum == 0 ? 1 : gridStep * 4;
            double X(int index) => 48 + 660d * index / Math.Max(1, starts.Count - 1);
            double Y(decimal value) => 14 + 154 * (1 - (double)value / scale);
            string C(double value) => ChartGeometry.Coordinate(value);
            var grid = Enumerable.Range(0, 5).Select(step => new ChartGrid(C(14 + 154d * step / 4), maximum > 0 ? axis(scale * (4 - step) / 4) : step == 4 ? "0" : "")).ToArray();
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
                    if ((sample == 0 || Missing(row, sample - 1)) && (sample == starts.Count - 1 || Missing(row, sample + 1)))
                    {
                        markers.Add(new(C(X(sample)), C(Y(row.Values[sample])), color, dash));
                    }
                }
                Flush(); paths.Add(new(row.Name, color, dash, string.Join(' ', segments)));
                void Flush() { if (segment.Count == 0) { return; } segments.Add(ChartGeometry.Smooth([.. segment])); segment.Clear(); }
            }
            var columns = new List<RequestColumn>();
            // One hover/keyboard column per bucket, so every plotted value (both endpoints included) is inspectable.
            var columnWidth = 660d / Math.Max(1, starts.Count - 1);
            for (var index = 0; index < starts.Count; index++)
            {
                var header = starts[index].UtcDateTime.ToString("MMM d HH:mm 'UTC'", CultureInfo.InvariantCulture);
                var rows = paths.Select((path, order) => new RequestTipRow(path.Name, Missing(series[order], index) ? "Not reported" : format(series[order].Values[index]), path.Class, path.Dash, C(14 + 6 + 17 * (order + 1.5)))).ToArray();
                var width = Math.Max(header.EnumerateRunes().Count() * 6.8, 32 + (rows.Max(row => row.Name.EnumerateRunes().Count()) + rows.Max(row => row.Value.EnumerateRunes().Count())) * 6.8 + 16) + 20;
                var left = X(index) + 10 + width > 708 ? Math.Max(0, X(index) - 10 - width) : X(index) + 10;
                var dots = paths.Select((path, order) => (path, order)).Where(value => !Missing(series[value.order], index)).Select(value => new RequestDot(C(X(index)), C(Y(series[value.order].Values[index])), value.path.Class, value.path.Dash)).ToArray();
                columns.Add(new(C(X(index)), C(Math.Max(48, X(index) - columnWidth / 2)), C(Math.Min(708, X(index) + columnWidth / 2) - Math.Max(48, X(index) - columnWidth / 2)), header + ": " + string.Join(", ", rows.Select(row => row.Name + " " + row.Value)), header, C(left), C(width), C(12 + (rows.Length + 1) * 17), dots, rows));
            }
            return new(label, series.Count == 1 && !(series[0].Missing?.Contains(true) ?? false) ? paths[0].Path + " L 708,168 L 48,168 Z" : "", grid, ticks, paths, markers, columns);
        }
    }
}
