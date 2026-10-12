using System.Text.RegularExpressions;
using TerraScale.Authentication.Features.Ui;

namespace TerraScale.Authentication.Features.Email;

/// <summary>One mode's email colours, each <c>#rrggbb</c>.</summary>
/// <param name="Canvas">Page background (<c>--ts-canvas</c>).</param>
/// <param name="Card">Card background (<c>--ts-surface</c>).</param>
/// <param name="Border">Card border and rules (<c>--ts-surface-3</c>).</param>
/// <param name="Heading">Heading and code (<c>--ts-ink</c>).</param>
/// <param name="Text">Body text (<c>--ts-ink-2</c>).</param>
/// <param name="Muted">Footnotes and footer (<c>--ts-ink-3</c>).</param>
/// <param name="Button">TerraScale's button fill (<c>--ts-primary</c>).</param>
/// <param name="Link">TerraScale's link colour (<c>--ts-link</c>).</param>
public sealed record EmailColors(string Canvas, string Card, string Border, string Heading, string Text, string Muted, string Button, string Link);

/// <summary>
/// The email colours in light and dark mode, read from the hosted pages' design tokens so pages and emails share one
/// palette. Email clients have no CSS variables or <c>light-dark()</c>, so the layout gets the resolved hex values.
/// </summary>
/// <param name="Light">Light mode.</param>
/// <param name="Dark">Dark mode.</param>
public sealed partial record EmailPalette(EmailColors Light, EmailColors Dark)
{
    /// <summary>The palette of the embedded tokens.</summary>
    public static EmailPalette Default { get; } = Parse(HostedLayoutModel.TokensCss);

    /// <summary>Reads the palette from a tokens stylesheet whose colours are <c>light-dark(#light, #dark)</c> or one <c>#hex</c>.</summary>
    public static EmailPalette Parse(string tokensCss)
    {
        ArgumentNullException.ThrowIfNull(tokensCss);
        (string Light, string Dark) Token(string name) =>
            Declaration().Matches(tokensCss).FirstOrDefault(match => match.Groups["name"].Value == name) is { } match
                ? (match.Groups["light"].Value, match.Groups["dark"].Success ? match.Groups["dark"].Value : match.Groups["light"].Value)
                : throw new InvalidOperationException($"The tokens have no hex --ts-{name}.");
        var names = new[] { "canvas", "surface", "surface-3", "ink", "ink-2", "ink-3", "primary", "link" };
        var values = names.Select(Token).ToArray();
        EmailColors Mode(Func<(string Light, string Dark), string> pick)
        {
            var colours = values.Select(pick).ToArray();
            return new EmailColors(colours[0], colours[1], colours[2], colours[3], colours[4], colours[5], colours[6], colours[7]);
        }

        return new EmailPalette(Mode(value => value.Light), Mode(value => value.Dark));
    }

    /// <summary>The dark-mode button: TerraScale's own button takes the dark token; a custom brand colour stays.</summary>
    public string ButtonDark(EmailBrand brand)
    {
        ArgumentNullException.ThrowIfNull(brand);
        return brand.PrimaryColor == this.Light.Button ? this.Dark.Button : brand.PrimaryColor;
    }

    /// <summary>Dark-mode links: TerraScale's own link takes the dark token; a custom accent is lightened to 4.5:1.</summary>
    public string LinkDark(EmailBrand brand)
    {
        ArgumentNullException.ThrowIfNull(brand);
        return brand.AccentColor == this.Light.Link ? this.Dark.Link : brand.LinkOn(this.Dark.Card, "#ffffff");
    }

    [GeneratedRegex(@"--ts-(?<name>[a-z0-9-]+):\s*(?:light-dark\(\s*(?<light>#[0-9a-fA-F]{6})\s*,\s*(?<dark>#[0-9a-fA-F]{6})\s*\)|(?<light>#[0-9a-fA-F]{6}))\s*;")]
    private static partial Regex Declaration();
}
