using System.Globalization;
using System.Text;

namespace TerraScale.Dashboard.Features.Home;

public sealed record ChartGrid(string Y, string Label);
public sealed record ChartTick(string X, string Label, string Anchor);
public sealed record ChartColumn(string X, string Y, string Left, string Width, string Header, string Value, string TipX, string TipWidth);
public sealed record OperationChart(string Label, string Path, string Area, IReadOnlyList<ChartGrid> Grid, IReadOnlyList<ChartTick> Ticks, IReadOnlyList<ChartColumn> Columns, string Name = "Operations")
{
    public static string Coordinate(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
    public static OperationChart For(HomePage page)
    {
        var rangeLabel = page.Range switch { "1h" => "Current UTC hour", "24h" => "Last 24 hours", "30d" => "Last 30 days", _ => "Last 7 days" };
        var label = page.Measurement.ActivityLabel + ", " + rangeLabel + ": " + page.Measurement.ActivityNote;
        var points = page.Measurement.Activity;
        if (points.Count == 0) return new(label, "", "", [], [], []);
        var values = points.Select(point => checked(point.Reads + point.Writes)).ToArray();
        var maximum = values.Max();
        double magnitude = 1;
        while (magnitude <= maximum / 10d) magnitude *= 10;
        var scale = maximum == 0 ? 1 : new[] { 1d, 2d, 5d, 10d }.Select(factor => factor * magnitude).First(value => value >= maximum);
        var coordinates = values.Select((value, index) => (X: 48 + 660d * index / Math.Max(1, values.Length - 1), Y: 14 + 134 * (1 - value / scale))).ToArray();
        var grid = Enumerable.Range(0, 5).Select(step => new ChartGrid(Coordinate(14 + 134d * step / 4), maximum > 0 ? Compact(scale * (4 - step) / 4) : step == 4 ? "0" : "")).ToArray();
        var seen = new HashSet<int>();
        var ticks = Enumerable.Range(0, 5).Select(step =>
        {
            var index = (int)Math.Round(step * (values.Length - 1) / 4d, MidpointRounding.AwayFromZero);
            var tick = "";
            if (page.Measurement.ActivityTicks.Count > step) tick = page.Measurement.ActivityTicks[step];
            else if ((points.Count == 1 || index != points.Count - 1 || step == 4) && seen.Add(index))
            {
                var age = page.Measurement.ObservedAt - points[index].Start;
                tick = age.TotalDays >= 1 ? (Math.Round(age.TotalDays * 10, MidpointRounding.AwayFromZero) / 10).ToString("G", CultureInfo.InvariantCulture) + "d" :
                    age.TotalHours >= 1 ? (Math.Round(age.TotalHours * 10, MidpointRounding.AwayFromZero) / 10).ToString("G", CultureInfo.InvariantCulture) + "h" :
                    age.TotalMinutes >= 1 ? ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m" : "Now";
                if (tick != "Now" && step is 0 or 4) tick += " ago";
            }
            return new ChartTick(Coordinate(coordinates[index].X), tick, step == 0 ? "start" : step == 4 ? "end" : "middle");
        }).ToArray();
        var path = Smooth(coordinates);
        var stepSize = Math.Max(1, (points.Count + 59) / 60);
        var columnWidth = 660d * stepSize / Math.Max(1, points.Count - 1);
        var columns = new List<ChartColumn>();
        for (var index = stepSize / 2; index < points.Count; index += stepSize)
        {
            var x = coordinates[index].X;
            var header = page.Measurement.ActivityLabels.Count > index ? page.Measurement.ActivityLabels[index] : points[index].Start.UtcDateTime.ToString(page.Range == "30d" ? "MMM d" : "MMM d, HH:mm 'UTC'", CultureInfo.InvariantCulture);
            var value = values[index].ToString("N0", CultureInfo.InvariantCulture);
            var width = Math.Max(header.Length * 6.8, 32 + (page.Measurement.ActivityLabel.Length + value.Length) * 6.8 + 16) + 20;
            var left = x + 10 + width > 708 ? Math.Max(0, x - 10 - width) : x + 10;
            columns.Add(new(Coordinate(x), Coordinate(coordinates[index].Y), Coordinate(Math.Max(48, x - columnWidth / 2)), Coordinate(Math.Min(708, x + columnWidth / 2) - Math.Max(48, x - columnWidth / 2)), header, value, Coordinate(left), Coordinate(width)));
        }
        return new(label, path, path + " L 708,148 L 48,148 Z", grid, ticks, columns, page.Measurement.ActivityLabel);
    }

    internal static string Compact(double value) => value >= 1000000 ? (value / 1000000).ToString("F1", CultureInfo.InvariantCulture) + "M" : value >= 1000 ? (value / 1000).ToString("F1", CultureInfo.InvariantCulture) + "k" : Math.Truncate(value).ToString(CultureInfo.InvariantCulture);
    internal static string Smooth((double X, double Y)[] points)
    {
        var path = new StringBuilder($"M {Coordinate(points[0].X)},{Coordinate(points[0].Y)}");
        if (points.Length < 3)
        {
            foreach (var point in points.Skip(1)) path.Append($" L {Coordinate(point.X)},{Coordinate(point.Y)}");
            return path.ToString();
        }
        var slopes = Enumerable.Range(0, points.Length - 1).Select(index => (points[index + 1].Y - points[index].Y) / (points[index + 1].X - points[index].X)).ToArray();
        var tangents = new double[points.Length];
        tangents[0] = slopes[0]; tangents[^1] = slopes[^1];
        for (var index = 1; index < points.Length - 1; index++) if (slopes[index - 1] * slopes[index] > 0) tangents[index] = 2 / (1 / slopes[index - 1] + 1 / slopes[index]);
        for (var index = 0; index < points.Length - 1; index++)
        {
            var dx = (points[index + 1].X - points[index].X) / 3;
            path.Append($" C {Coordinate(points[index].X + dx)},{Coordinate(points[index].Y + tangents[index] * dx)} {Coordinate(points[index + 1].X - dx)},{Coordinate(points[index + 1].Y - tangents[index + 1] * dx)} {Coordinate(points[index + 1].X)},{Coordinate(points[index + 1].Y)}");
        }
        return path.ToString();
    }
}
