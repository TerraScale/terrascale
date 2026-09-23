using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Html;

namespace TerraScale.Dashboard.Shared.Components;

/// <summary>Shared dotted-map component. Every region map in the dashboard —
/// the onboarding project step, the organization project forms, the database
/// create dialog, and the decorative auth backdrop — renders through here, so
/// the frame, dotted canvas, pins, hover labels, and coordinate table can
/// never drift apart. Coordinates are dotted-map (vanDerGrinten, 136x80)
/// projections — regenerate with tools/generate-region-map.mjs, never edit by
/// hand.</summary>
public static class RegionMap
{
    public const double Width = 136;
    public const double Height = 80;

    /// <summary>How the pins accept input: one radio (onboarding), many
    /// checkboxes (project placement), or none (read-only display).</summary>
    public enum Selection
    {
        None,
        Single,
        Multiple,
    }

    /// <summary>One pin on the map canvas. X/Y are the dotted-map units;
    /// Country/LatencyMs feed the hover label.</summary>
    public sealed record Pin(
        string Code,
        string Name,
        string Country,
        int LatencyMs,
        double X,
        double Y,
        bool Selected = false,
        bool Disabled = false);

    private sealed record Projection(string Oci, double X, double Y);

    private static readonly Dictionary<string, Projection> Regions = new()
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

    /// <summary>Projected 136x80 coordinates for a region code (matched
    /// case-insensitively), or null when the map has no pin. Shared so the
    /// onboarding region catalog keeps no second copy of the pin table.</summary>
    public static (double X, double Y)? Coordinates(string code) =>
        Regions.GetValueOrDefault(code.ToUpperInvariant()) is { } pin ? (pin.X, pin.Y) : null;

    /// <summary>Renders the shared region map: the dotted canvas plus a pin per
    /// region with its hover label (country flag, name, round-trip latency).
    /// <see cref="Selection.Single"/> renders a radiogroup, <see cref="Selection.Multiple"/>
    /// a checkbox group, and <see cref="Selection.None"/> a read-only map. Every
    /// mode also emits an sr-only region list, keeping a non-map control as the
    /// canonical, accessible description of the selection.</summary>
    public static HtmlString Picker(
        IReadOnlyList<Pin> pins,
        Selection selection = Selection.Multiple,
        string name = "regions",
        string? ariaLabel = null,
        string? ariaLabelledBy = null)
    {
        var html = new StringBuilder();
        var role = selection switch
        {
            Selection.Single => " role=\"radiogroup\"",
            Selection.Multiple => " role=\"group\"",
            _ => "",
        };
        html.Append("<div class=\"ts-region-frame\"").Append(role);
        if (ariaLabel is { Length: > 0 })
        {
            html.Append(" aria-label=\"").Append(Escape(ariaLabel)).Append('"');
        }

        if (ariaLabelledBy is { Length: > 0 })
        {
            html.Append(" aria-labelledby=\"").Append(Escape(ariaLabelledBy)).Append('"');
        }

        html.Append('>');
        html.Append("<img class=\"ts-region-surface\" src=\"")
            .Append(AssetUrls.Url("/brand/region-map.svg"))
            .Append("\" alt=\"\" decoding=\"async\" />");

        foreach (var pin in pins)
        {
            // Pins in the northern third flip the hover label below the marker
            // so the frame's overflow clip cannot cut it off at the top edge;
            // pins near the side edges anchor the label to that edge instead of
            // centring it past the clip.
            var edge = pin.X / Width;
            var classes = "ts-region-marker"
                + (pin.Disabled ? " ts-region-marker-disabled" : "")
                + (selection == Selection.None && pin.Selected ? " ts-region-marker-selected" : "")
                + (selection == Selection.None ? " ts-region-marker-readonly" : "")
                + (pin.Y < Height / 3 ? " ts-region-marker-label-below" : "")
                + (edge < 0.25 ? " ts-region-marker-label-start" : "")
                + (edge > 0.75 ? " ts-region-marker-label-end" : "");
            var tag = selection == Selection.None ? "span" : "label";

            html.Append('<').Append(tag).Append(" class=\"").Append(classes)
                .Append("\" style=\"left:").Append(Percent(pin.X, Width))
                .Append("%;top:").Append(Percent(pin.Y, Height)).Append("%\">");

            if (selection != Selection.None)
            {
                html.Append("<input class=\"ts-region-marker-input\" type=\"")
                    .Append(selection == Selection.Single ? "radio" : "checkbox")
                    .Append("\" name=\"").Append(Escape(name))
                    .Append("\" value=\"").Append(Escape(pin.Code))
                    .Append("\" aria-label=\"").Append(Escape(Describe(pin))).Append('"');
                if (pin.Selected) html.Append(" checked");
                if (pin.Disabled) html.Append(" disabled");
                html.Append(" />");
            }

            html.Append("<span class=\"ts-region-marker-dot\" aria-hidden=\"true\"></span>");
            html.Append("<span class=\"ts-region-marker-label\" aria-hidden=\"true\">");
            if (pin.Country is { Length: > 0 })
            {
                html.Append("<img class=\"ts-region-marker-flag\" src=\"")
                    .Append(AssetUrls.Url($"/brand/flags/{pin.Country.ToLowerInvariant()}.svg"))
                    .Append("\" alt=\"\" decoding=\"async\" />");
            }

            html.Append("<span class=\"ts-region-marker-label-name\">").Append(Escape(pin.Name)).Append("</span>")
                .Append("<span class=\"ts-region-marker-label-ping\">").Append(pin.LatencyMs).Append(" ms</span>")
                .Append("</span>")
                .Append("</").Append(tag).Append('>');
        }

        html.Append("</div>");
        html.Append("<ul class=\"ts-sr-only ts-region-list\" aria-label=\"Regions\">");
        foreach (var pin in pins)
        {
            html.Append("<li>").Append(Escape(pin.Name))
                .Append(" (").Append(pin.Code.ToUpperInvariant()).Append(") — ")
                .Append(Status(pin)).Append("</li>");
        }

        html.Append("</ul>");
        return new HtmlString(html.ToString());
    }

    /// <summary>Decorative overlay for the auth backdrop: animated connection
    /// arcs plus fixed-size pulsing nodes (HTML, percent-positioned so they
    /// never scale with the map). Not a form control. Unknown codes are
    /// skipped.</summary>
    public static HtmlString Highlight(IReadOnlyCollection<string> codes)
    {
        var nodes = codes.Select(code => (code, pin: Regions.GetValueOrDefault(code)))
            .Where(t => t.pin is not null)
            .Select(t => (t.code, pin: t.pin!))
            .ToList();
        if (nodes.Count == 0) return HtmlString.Empty;

        var html = new StringBuilder()
            .Append("<svg class=\"ts-region-overlay\" viewBox=\"0 0 ")
            .Append(Width).Append(' ').Append(Height)
            .Append("\" preserveAspectRatio=\"xMidYMid meet\" aria-hidden=\"true\">");
        for (var i = 0; i < nodes.Count; i++)
        {
            var from = nodes[i].pin;
            var to = nodes[(i + 1) % nodes.Count].pin;
            html.Append("<path class=\"ts-region-arc\" style=\"animation-delay:").Append(i * 0.45).Append("s\" d=\"")
                .Append(ArcPath(from, to))
                .Append("\" />");
        }

        html.Append("</svg>");
        for (var i = 0; i < nodes.Count; i++)
        {
            var (code, pin) = nodes[i];
            html.Append("<span class=\"ts-region-node\" id=\"ts-region-").Append(code)
                .Append("\" data-oci=\"").Append(pin.Oci)
                .Append("\" style=\"left:").Append(Percent(pin.X, Width))
                .Append("%;top:").Append(Percent(pin.Y, Height)).Append("%\">")
                .Append("<span class=\"ts-region-node-ring\" style=\"animation-delay:")
                .Append((i * 0.3).ToString("0.##", CultureInfo.InvariantCulture)).Append("s\"></span>")
                .Append("<span class=\"ts-region-node-core\"></span>")
                .Append("</span>");
        }

        return new HtmlString(html.ToString());
    }

    private static string Describe(Pin pin) =>
        $"{pin.Name} ({pin.Code.ToUpperInvariant()}) — {Status(pin)}, {pin.LatencyMs} ms";

    private static string Status(Pin pin) =>
        pin.Disabled ? "Unavailable" : pin.Selected ? "Selected" : "Available";

    private static string ArcPath(Projection from, Projection to)
    {
        var midX = (from.X + to.X) / 2;
        var length = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        var lift = Math.Max(3, length * 0.18);
        var controlY = Math.Min(from.Y, to.Y) - lift - Math.Abs(to.X - from.X) * 0.04;
        return $"M {from.X.ToString("0.####", CultureInfo.InvariantCulture)} {from.Y.ToString("0.####", CultureInfo.InvariantCulture)} Q {midX.ToString("0.####", CultureInfo.InvariantCulture)} {controlY.ToString("0.####", CultureInfo.InvariantCulture)} {to.X.ToString("0.####", CultureInfo.InvariantCulture)} {to.Y.ToString("0.####", CultureInfo.InvariantCulture)}";
    }

    private static string Percent(double value, double total) =>
        (value / total * 100).ToString("0.####", CultureInfo.InvariantCulture);

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
