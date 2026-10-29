using System.Globalization;
using TerraScale.Dashboard.Common.Charts;

namespace TerraScale.Dashboard.Features.Home
{
    public sealed record HomeRequestPoint(DateTimeOffset Start, IReadOnlyDictionary<string, long> Requests);
    public sealed record HomeRequestChart(string Label, string Area, IReadOnlyList<ChartGrid> Grid, IReadOnlyList<ChartTick> Ticks, IReadOnlyList<RequestPath> Paths, IReadOnlyList<RequestColumn> Columns)
    {
        public static HomeRequestChart For(HomePage page)
        {
            var points = page.Measurement.RequestPoints;
            var label = "Requests by type, " + page.Range;
            var names = points.SelectMany(point => point.Requests.Keys).Distinct().Order(StringComparer.Ordinal).ToArray();
            if (points.Count == 0 || names.Length == 0)
            {
                return new(label, "", [], [], [], []);
            }

            var maximum = points.SelectMany(point => point.Requests.Values).DefaultIfEmpty().Max();
            double magnitude = 1;
            while (magnitude <= maximum / 10d)
            {
                magnitude *= 10;
            }

            var scale = maximum == 0 ? 1 : new[] { 1d, 2d, 5d, 10d }.Select(factor => factor * magnitude).First(value => value >= maximum);
            double X(int index) => 48 + 660d * index / Math.Max(1, points.Count - 1);
            double Y(long value) => 14 + 154 * (1 - value / scale);
            string C(double value) => ChartGeometry.Coordinate(value);
            var grid = Enumerable.Range(0, 5).Select(step => new ChartGrid(C(14 + 154d * step / 4), maximum > 0 ? ChartGeometry.Compact(scale * (4 - step) / 4) : step == 4 ? "0" : "")).ToArray();
            var seen = new HashSet<int>();
            var ticks = Enumerable.Range(0, 5).Select(step =>
            {
                var index = (int)Math.Round(step * (points.Count - 1) / 4d, MidpointRounding.AwayFromZero);
                var visible = (points.Count == 1 || index != points.Count - 1 || step == 4) && seen.Add(index);
                return new ChartTick(C(X(index)), visible ? points[index].Start.UtcDateTime.ToString(page.Range is "7d" or "30d" ? "MMM d HH:mm" : "HH:mm", CultureInfo.InvariantCulture) : "", step == 0 ? "start" : step == 4 ? "end" : "middle");
            }).ToArray();
            var paths = names.Select((name, index) => new RequestPath(name, names.Length == 1 ? "ts-chart-series-1" : "ts-cat-" + (index % 3 + 1), index < 3 ? "" : 3 * (index / 3 + 1) + " 5",
                ChartGeometry.Smooth([.. points.Select((point, i) => (X(i), Y(point.Requests.GetValueOrDefault(name))))]))).ToArray();
            // One hover/keyboard column per bucket, so every plotted value (both endpoints included) is inspectable.
            var columnWidth = 660d / Math.Max(1, points.Count - 1);
            var columns = new List<RequestColumn>();
            for (var index = 0; index < points.Count; index++)
            {
                var header = points[index].Start.UtcDateTime.ToString("MMM d HH:mm 'UTC'", CultureInfo.InvariantCulture);
                var rows = paths.Select((path, i) => new RequestTipRow(path.Name, points[index].Requests.GetValueOrDefault(path.Name).ToString("N0", CultureInfo.InvariantCulture), path.Class, path.Dash, C(14 + 6 + 17 * (i + 1.5)))).ToArray();
                var width = Math.Max(header.Length * 6.8, 32 + (rows.Max(row => row.Name.EnumerateRunes().Count()) + rows.Max(row => row.Value.Length)) * 6.8 + 16) + 20;
                var left = X(index) + 10 + width > 708 ? Math.Max(0, X(index) - 10 - width) : X(index) + 10;
                var dots = paths.Select(path => new RequestDot(C(X(index)), C(Y(points[index].Requests.GetValueOrDefault(path.Name))), path.Class, path.Dash)).ToArray();
                columns.Add(new(C(X(index)), C(Math.Max(48, X(index) - columnWidth / 2)), C(Math.Min(708, X(index) + columnWidth / 2) - Math.Max(48, X(index) - columnWidth / 2)), header + ": " + string.Join(", ", rows.Select(row => row.Name + " " + row.Value)), header, C(left), C(width), C(12 + (rows.Length + 1) * 17), dots, rows));
            }
            return new(label, paths.Length == 1 ? paths[0].Path + " L 708,168 L 48,168 Z" : "", grid, ticks, paths, columns);
        }
    }
}
