using System.Text;
using Microsoft.AspNetCore.Html;

namespace TerraScale.Dashboard2;

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

    /// <summary>Inline SVG pin overlay; arcs connect consecutive regions
    /// (ring), matching the auth page visual. Unknown codes are skipped.</summary>
    public static HtmlString Highlight(IReadOnlyCollection<string> codes)
    {
        var pins = codes.Select(code => (code, pin: Regions.GetValueOrDefault(code)))
            .Where(t => t.pin is not null)
            .Select(t => (t.code, pin: t.pin!))
            .ToList();
        if (pins.Count == 0) return HtmlString.Empty;

        var svg = new StringBuilder()
            .Append("<svg class=\"ts-region-pins\" viewBox=\"0 0 ")
            .Append(Width).Append(' ').Append(Height)
            .Append("\" preserveAspectRatio=\"xMidYMid meet\" aria-hidden=\"true\">");
        for (var i = 0; i < pins.Count; i++)
        {
            var from = pins[i].pin;
            var to = pins[(i + 1) % pins.Count].pin;
            svg.Append("<path fill=\"none\" stroke=\"rgba(37, 99, 235, 0.7)\" stroke-dasharray=\"2 3\" stroke-linecap=\"round\" stroke-width=\"0.22\" opacity=\"0.92\" d=\"")
               .Append(ArcPath(from, to))
               .Append("\" />");
        }
        foreach (var (code, pin) in pins)
        {
            svg.Append("<g id=\"ts-region-").Append(code)
               .Append("\" class=\"ts-region-pin\" data-oci=\"").Append(pin.Oci)
               .Append("\" transform=\"translate(").Append(pin.X.ToString("0.##")).Append(' ').Append(pin.Y.ToString("0.##")).Append(")\">")
               .Append("<circle r=\"7\" fill=\"").Append(AccentSoft).Append("\" opacity=\"0.22\" />")
               .Append("<circle r=\"3.2\" fill=\"none\" stroke=\"rgba(37, 99, 235, 0.82)\" stroke-width=\"0.6\" />")
               .Append("<circle r=\"3.2\" fill=\"").Append(AccentSoft).Append("\" /></g>");
        }
        return new HtmlString(svg.Append("</svg>").ToString());
    }

    private const string AccentSoft = "#60A5FA";

    private static string ArcPath(Pin from, Pin to)
    {
        var midX = (from.X + to.X) / 2;
        var length = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        var lift = Math.Max(3, length * 0.18);
        var controlY = Math.Min(from.Y, to.Y) - lift - Math.Abs(to.X - from.X) * 0.04;
        return $"M {from.X.ToString("0.####")} {from.Y.ToString("0.####")} Q {midX.ToString("0.####")} {controlY.ToString("0.####")} {to.X.ToString("0.####")} {to.Y.ToString("0.####")}";
    }
}
