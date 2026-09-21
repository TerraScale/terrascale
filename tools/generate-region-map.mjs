// Generates the dotted world map + the C# region pin table for TerraScale.Dashboard.
//
//   cd tools && npm ci && node generate-region-map.mjs
//
// Run by hand when the region list changes — the outputs are committed, so the
// dashboard build never needs npm (see tools/README.md).
import { default as DottedMapDefault } from "dotted-map";

const DottedMap = DottedMapDefault.default ?? DottedMapDefault;
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const ACCENT_SOFT = "#60A5FA";
const ACCENT_RGB = "37, 99, 235";

// Every OCI commercial region we may light up, plus the TerraScale trio the
// seeded demo data uses. [code, oci region id, lat, lng]
const regions = [
  ["SFO", "legacy", 37.7749, -122.4194],
  ["GRU", "sa-saopaulo-1", -23.5475, -46.6361],
  ["FRA", "eu-frankfurt-1", 50.1109, 8.6821],
  ["IAD", "us-ashburn-1", 39.0403, -77.4903],
  ["PHX", "us-phoenix-1", 33.4484, -112.074],
  ["SJC", "us-sanjose-1", 37.3382, -121.8863],
  ["ORD", "us-chicago-1", 41.8781, -87.6298],
  ["YYZ", "ca-toronto-1", 43.6532, -79.3832],
  ["YUL", "ca-montreal-1", 45.5019, -73.5674],
  ["LHR", "uk-london-1", 51.5074, -0.1278],
  ["ZRH", "eu-zurich-1", 47.3769, 8.5417],
  ["AMS", "eu-amsterdam-1", 52.3676, 4.9041],
  ["CDG", "eu-paris-1", 48.8566, 2.3522],
  ["MRS", "eu-marseille-1", 43.2965, 5.3698],
  ["LIN", "eu-milan-1", 45.4642, 9.19],
  ["MAD", "eu-madrid-1", 40.4168, -3.7038],
  ["ARN", "eu-stockholm-1", 59.3293, 18.0686],
  ["JED", "me-jeddah-1", 21.4858, 39.1925],
  ["DXB", "me-dubai-1", 25.2048, 55.2708],
  ["AUH", "me-abudhabi-1", 24.4539, 54.3773],
  ["JNB", "af-johannesburg-1", -26.2041, 28.0473],
  ["JRS", "il-jerusalem-1", 31.7683, 35.2137],
  ["MTY", "mx-monterrey-1", 25.6866, -100.3161],
  ["VCP", "sa-vinhedo-1", -23.0295, -46.9937],
  ["SCL", "sa-santiago-1", -33.4489, -70.6693],
  ["BOG", "sa-bogota-1", 4.711, -74.0721],
  ["BOM", "ap-mumbai-1", 19.076, 72.8777],
  ["HYD", "ap-hyderabad-1", 17.385, 78.4867],
  ["SIN", "ap-singapore-1", 1.3521, 103.8198],
  ["NRT", "ap-tokyo-1", 35.6762, 139.6503],
  ["KIX", "ap-osaka-1", 34.6937, 135.5023],
  ["CJC", "ap-chuncheon-1", 37.8808, 127.727],
  ["ICN", "ap-seoul-1", 37.5665, 126.978],
  ["SYD", "ap-sydney-1", -33.8688, 151.2093],
  ["MEL", "ap-melbourne-1", -37.8136, 144.9631],
];

// Dots-only map (pins live in the C# overlay so pages can highlight regions).
const dots = new DottedMap({
  height: 80,
  width: 136,
  grid: "diagonal",
  projection: { name: "vanDerGrinten" },
});
const dotsSvg = dots.getSVG({
  radius: 0.18,
  color: `rgba(${ACCENT_RGB}, 0.52)`,
  backgroundColor: "transparent",
});

// Projected pin coordinates for every region.
const withPins = new DottedMap({
  height: 80,
  width: 136,
  grid: "diagonal",
  projection: { name: "vanDerGrinten" },
});
for (const [code, oci, lat, lng] of regions) {
  withPins.addPin({ lat, lng, data: { code } });
}
const byCode = new Map(
  withPins
    .getPoints()
    .filter((p) => p?.data?.code)
    .map((p) => [p.data.code, p]),
);

const rows = regions
  .map(([code, oci]) => {
    const p = byCode.get(code);
    if (p === undefined) throw new Error(`dotted-map dropped region ${code}`);
    return `        ["${code}"] = new("${oci}", ${+p.x.toFixed(2)}, ${+p.y.toFixed(2)}),`;
  })
  .join("\n");

const cs = `using System.Text;
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
${rows}
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
            .Append("<svg class=\\"ts-region-pins\\" viewBox=\\"0 0 ")
            .Append(Width).Append(' ').Append(Height)
            .Append("\\" preserveAspectRatio=\\"xMidYMid meet\\" aria-hidden=\\"true\\">");
        for (var i = 0; i < pins.Count; i++)
        {
            var from = pins[i].pin;
            var to = pins[(i + 1) % pins.Count].pin;
            svg.Append("<path fill=\\"none\\" stroke=\\"rgba(37, 99, 235, 0.7)\\" stroke-dasharray=\\"2 3\\" stroke-linecap=\\"round\\" stroke-width=\\"0.22\\" opacity=\\"0.92\\" d=\\"")
               .Append(ArcPath(from, to))
               .Append("\\" />");
        }
        foreach (var (code, pin) in pins)
        {
            svg.Append("<g id=\\"ts-region-").Append(code)
               .Append("\\" class=\\"ts-region-pin\\" data-oci=\\"").Append(pin.Oci)
               .Append("\\" transform=\\"translate(").Append(pin.X.ToString("0.##")).Append(' ').Append(pin.Y.ToString("0.##")).Append(")\\">")
               .Append("<circle r=\\"7\\" fill=\\"").Append(AccentSoft).Append("\\" opacity=\\"0.22\\" />")
               .Append("<circle r=\\"3.2\\" fill=\\"none\\" stroke=\\"rgba(37, 99, 235, 0.82)\\" stroke-width=\\"0.6\\" />")
               .Append("<circle r=\\"3.2\\" fill=\\"").Append(AccentSoft).Append("\\" /></g>");
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
`;

mkdirSync(join(ROOT, "wwwroot", "brand"), { recursive: true });
writeFileSync(join(ROOT, "wwwroot", "brand", "region-map.svg"), dotsSvg);
writeFileSync(join(ROOT, "Framework", "RegionMap.cs"), cs);
console.log(`region-map.svg (dots) + RegionMap.cs (${regions.length} regions incl. SFO/GRU/FRA)`);
