using System.Globalization;
using System.Text;

namespace TerraScale.Dashboard.Shared.Charts;

public sealed record ChartGrid(string Y, string Label);
public sealed record ChartTick(string X, string Label, string Anchor);
public sealed record RequestPath(string Name, string Class, string Dash, string Path);
public sealed record RequestDot(string X, string Y, string Class, string Dash);
public sealed record RequestTipRow(string Name, string Value, string Class, string Dash, string Y);
public sealed record RequestColumn(string X, string Left, string Width, string Label, string Header, string TipX, string TipWidth, string TipHeight, IReadOnlyList<RequestDot> Dots, IReadOnlyList<RequestTipRow> Rows);

public static class ChartGeometry
{
    public static string Coordinate(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

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
