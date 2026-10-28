using System.Globalization;
using System.Text;
using TerraScale.Dashboard.Shared.Charts;
using static TerraScale.Dashboard.Shared.Charts.ChartGeometry;

namespace TerraScale.Dashboard.Features.Home;

public sealed record ChartColumn(string X, string Y, string Left, string Width, string Header, string Value, string TipX, string TipWidth);
public sealed record OperationChart(string Label, string Path, string Area, IReadOnlyList<ChartGrid> Grid, IReadOnlyList<ChartTick> Ticks, IReadOnlyList<ChartColumn> Columns, string Name = "Operations")
{
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
        // One hover/keyboard column per bucket, so every plotted value (both endpoints included) is inspectable.
        var columnWidth = 660d / Math.Max(1, points.Count - 1);
        var columns = new List<ChartColumn>();
        for (var index = 0; index < points.Count; index++)
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
}
