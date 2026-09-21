using System.Text;
using Microsoft.AspNetCore.Html;

namespace TerraScale.Dashboard;

/// <summary>Dotted world map overlay: renders highlight pins for TerraScale
/// regions on top of /brand/region-map.svg. Coordinates are projections from
/// dotted-map (vanDerGrinten, 136x80) — regenerate with
/// tools/generate-region-map.mjs, never edit by hand.</summary>
public static class RegionMap
{
    public const double Width = 136;
    public const double Height = 80;

    private sealed record Pin(string Oci, double X, double Y);

    private static readonly Dictionary<string, Pin> Regions = new()
    {
        ["SFO"] = new("legacy", 21, 28.58),
        ["GRU"] = new("sa-saopaulo-1", 50, 59.76),
        ["FRA"] = new("eu-frankfurt-1", 71.5, 20.78),
        ["IAD"] = new("us-ashburn-1", 39, 28.58),
        ["PHX"] = new("us-phoenix-1", 25.5, 31.18),
        ["SJC"] = new("us-sanjose-1", 21.5, 29.44),
        ["ORD"] = new("us-chicago-1", 34.5, 25.98),
        ["YYZ"] = new("ca-toronto-1", 38, 25.11),
        ["YUL"] = new("ca-montreal-1", 40.5, 24.25),
        ["LHR"] = new("uk-london-1", 68, 19.92),
        ["ZRH"] = new("eu-zurich-1", 71.5, 22.52),
        ["AMS"] = new("eu-amsterdam-1", 69.5, 19.05),
        ["CDG"] = new("eu-paris-1", 69, 21.65),
        ["MRS"] = new("eu-marseille-1", 70, 25.11),
        ["LIN"] = new("eu-milan-1", 71.5, 24.25),
        ["MAD"] = new("eu-madrid-1", 66.5, 27.71),
        ["ARN"] = new("eu-stockholm-1", 75, 12.99),
        ["JED"] = new("me-jeddah-1", 82.5, 38.11),
        ["DXB"] = new("me-dubai-1", 89, 35.51),
        ["AUH"] = new("me-abudhabi-1", 88.5, 36.37),
        ["JNB"] = new("af-johannesburg-1", 78.5, 60.62),
        ["JRS"] = new("il-jerusalem-1", 81, 32.04),
        ["MTY"] = new("mx-monterrey-1", 30, 35.51),
        ["VCP"] = new("sa-vinhedo-1", 50.5, 58.89),
        ["SCL"] = new("sa-santiago-1", 41, 64.95),
        ["BOG"] = new("sa-bogota-1", 40, 45.9),
        ["BOM"] = new("ap-mumbai-1", 96, 38.97),
        ["HYD"] = new("ap-hyderabad-1", 97.5, 39.84),
        ["SIN"] = new("ap-singapore-1", 107, 47.63),
        ["NRT"] = new("ap-tokyo-1", 121, 30.31),
        ["KIX"] = new("ap-osaka-1", 119, 30.31),
        ["CJC"] = new("ap-chuncheon-1", 117, 28.58),
        ["ICN"] = new("ap-seoul-1", 116.5, 29.44),
        ["SYD"] = new("ap-sydney-1", 125, 64.95),
        ["MEL"] = new("ap-melbourne-1", 123.5, 67.55),
    };

    /// <summary>Region overlay for the auth map: animated connection arcs
    /// (inline SVG) plus fixed-size pulsing pins (HTML, percent-positioned so
    /// they never scale with the map). Unknown codes are skipped.</summary>
    public static HtmlString Highlight(IReadOnlyCollection<string> codes)
    {
        var pins = codes.Select(code => (code, pin: Regions.GetValueOrDefault(code)))
            .Where(t => t.pin is not null)
            .Select(t => (t.code, pin: t.pin!))
            .ToList();
        if (pins.Count == 0) return HtmlString.Empty;

        var html = new StringBuilder()
            .Append("<svg class=\"ts-region-pins\" viewBox=\"0 0 ")
            .Append(Width).Append(' ').Append(Height)
            .Append("\" preserveAspectRatio=\"xMidYMid meet\" aria-hidden=\"true\">");
        for (var i = 0; i < pins.Count; i++)
        {
            var from = pins[i].pin;
            var to = pins[(i + 1) % pins.Count].pin;
            html.Append("<path class=\"ts-region-arc\" style=\"animation-delay:").Append(i * 0.45).Append("s\" d=\"")
                .Append(ArcPath(from, to))
                .Append("\" />");
        }
        html.Append("</svg>");
        for (var i = 0; i < pins.Count; i++)
        {
            var (code, pin) = pins[i];
            var left = (pin.X / Width * 100).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            var top = (pin.Y / Height * 100).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            html.Append("<span class=\"ts-region-pin\" id=\"ts-region-").Append(code)
                .Append("\" data-oci=\"").Append(pin.Oci)
                .Append("\" style=\"left:").Append(left).Append("%;top:").Append(top).Append("%\">")
                .Append("<span class=\"ts-region-pin-ring\" style=\"animation-delay:").Append((i * 0.3).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append("s\"></span>")
                .Append("<span class=\"ts-region-pin-core\"></span>")
                .Append("</span>");
        }
        return new HtmlString(html.ToString());
    }

    private static string ArcPath(Pin from, Pin to)
    {
        var midX = (from.X + to.X) / 2;
        var length = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        var lift = Math.Max(3, length * 0.18);
        var controlY = Math.Min(from.Y, to.Y) - lift - Math.Abs(to.X - from.X) * 0.04;
        return $"M {from.X.ToString("0.####")} {from.Y.ToString("0.####")} Q {midX.ToString("0.####")} {controlY.ToString("0.####")} {to.X.ToString("0.####")} {to.Y.ToString("0.####")}";
    }
}
