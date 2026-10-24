using System.Globalization;

namespace TerraScale.Authentication.Features.Ui;

/// <summary>WCAG luminance derivation from the approved v3 brand.js, rendered under the page's CSP nonce.</summary>
internal static class HostedPalette
{
    private static readonly Color White = new(255, 255, 255);
    private static readonly Color Ink = new(10, 10, 10);

    public static string Css(string? accent, string? background, string? text)
    {
        if (accent is null && background is null && text is null)
        {
            return string.Empty;
        }

        var light = Derive(accent ?? HostedLayoutModel.DefaultAccent, text, false);
        var dark = Derive(accent ?? HostedLayoutModel.DefaultAccent, text, true);
        // A page background never replaces the canvas used by chips inside the card.
        var footer = background is null ? string.Empty : Footer(background, text);
        var links = (light.Underline ? ":root[data-theme=light] .card a:not(.btn){text-decoration:underline}" : string.Empty)
            + (dark.Underline ? ":root[data-theme=dark] .card a:not(.btn){text-decoration:underline}" : string.Empty);
        return footer + links + $":root{{--ink:light-dark({light.Ink},{dark.Ink});--accent:light-dark({light.Fill},{dark.Fill});--on-accent:light-dark({light.On},{dark.On});--accent-hover:light-dark({light.Hover},{dark.Hover});--link:light-dark({light.Link},{dark.Link})}}";
    }

    private static string Footer(string background, string? text)
    {
        var canvas = Parse(background);
        var away = On(canvas);
        // Mid-grey can fail against both white and the design's near-black ink.
        if (Contrast(away, canvas) < 4.5)
        {
            away = new(0, 0, 0);
        }

        var ink = Hex(Push(text is null ? away : Parse(text), away, color => Contrast(color, canvas) >= 4.5));
        return $":root{{--page-background:{background}}}.auth-foot{{--ink:{ink};--ink-2:{ink};--ink-3:{ink};--link:{ink};--control-line:{ink}}}";
    }

    private static (string Fill, string On, string Hover, string Link, string Ink, bool Underline) Derive(string accent, string? text, bool dark)
    {
        Color[] surfaces = [Parse(dark ? "#151515" : "#ffffff"), Parse(dark ? "#1b1b1b" : "#fafafa"), Parse(dark ? "#222222" : "#f4f4f5")];
        var baseColor = Parse(accent);
        var away = dark ? White : Ink;
        var ink = text is null ? Parse(dark ? "#f2f2f2" : "#0a0a0a")
            : Push(Parse(text), away, color => surfaces.All(surface => Contrast(color, surface) >= 4.5));
        var fill = Push(baseColor, away, color => surfaces.Take(2).All(surface => Contrast(color, surface) >= 3)
            && Contrast(color, On(color)) >= 4.5);
        var on = On(fill);
        var link = Push(baseColor, away, color => surfaces.All(surface => Contrast(color, surface) >= 4.5));
        return (Hex(fill), Hex(on), Hex(Mix(fill, on == White ? Ink : White, 0.14)), Hex(link), Hex(ink), Contrast(link, ink) < 3);
    }

    private static Color On(Color color) => Contrast(color, White) >= Contrast(color, Ink) ? White : Ink;

    private static Color Push(Color color, Color target, Func<Color, bool> acceptable)
    {
        // Test the emitted byte values: rounding must not move a passing ratio below AA.
        for (var step = 0; step < 60 && !acceptable(Round(color)); step++)
        {
            color = Mix(color, target, 0.06);
        }

        return Round(color);
    }

    private static Color Round(Color color) => new(Channel(color.R), Channel(color.G), Channel(color.B));

    private static Color Parse(string hex) => new(byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static string Hex(Color color) => $"#{Channel(color.R):x2}{Channel(color.G):x2}{Channel(color.B):x2}";

    private static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 255), MidpointRounding.AwayFromZero);

    private static Color Mix(Color color, Color target, double fraction) => new(color.R + ((target.R - color.R) * fraction),
        color.G + ((target.G - color.G) * fraction), color.B + ((target.B - color.B) * fraction));

    private static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color color) => (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    private static double Linear(double channel)
    {
        var value = channel / 255;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private readonly record struct Color(double R, double G, double B);
}
