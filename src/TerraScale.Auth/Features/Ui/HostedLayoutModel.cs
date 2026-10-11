using System.Text.RegularExpressions;

namespace TerraScale.Auth.Features.Ui;

/// <summary>
/// Model for the shared hosted-page card.
/// Razor encodes every value; the accent colour is additionally restricted to <c>#rrggbb</c>
/// because it is interpolated into CSS.
/// </summary>
public sealed partial record HostedLayoutModel
{
    /// <summary>Default accent used when an instance has no valid theme colour.</summary>
    public const string DefaultAccent = "#6c5ce7";

    /// <summary>Creates a model, discarding a theme colour that is not exactly <c>#rrggbb</c>.</summary>
    public HostedLayoutModel(string title, string nonce, string? themeColor = null, string? logoUrl = null)
    {
        this.Title = title;
        this.Nonce = nonce;
        this.LogoUrl = logoUrl;
        this.AccentColor = themeColor is null && logoUrl is null ? null
            : themeColor is not null && HexColor().IsMatch(themeColor) ? themeColor
            : DefaultAccent;
    }

    /// <summary>Page title, shown as <c>{title} · ts-auth</c>.</summary>
    public string Title { get; }

    /// <summary>Per-response CSP nonce for inline styles and scripts.</summary>
    public string Nonce { get; }

    /// <summary>Validated accent colour override, or <see langword="null"/> for the default.</summary>
    public string? AccentColor { get; }

    /// <summary>Instance logo URL, rendered as an encoded attribute.</summary>
    public string? LogoUrl { get; }

    [GeneratedRegex(@"\A#[0-9A-Fa-f]{6}\z")]
    private static partial Regex HexColor();

    /// <summary>The shared hosted-page stylesheet.</summary>
    public const string PageCss = """
:root { color-scheme: light dark; --accent: #6c5ce7; }
* { box-sizing: border-box; }
body { font: 16px/1.5 system-ui, sans-serif; margin: 0; display: grid; place-items: center; min-height: 100vh; background: #f6f7f9; color: #16191d; }
@media (prefers-color-scheme: dark) { body { background: #101418; color: #e8eaed; } }
.card { background: #fff; border: 1px solid #dfe3e8; border-radius: 12px; padding: 32px; width: min(400px, 92vw); box-shadow: 0 8px 30px rgba(0,0,0,.06); }
@media (prefers-color-scheme: dark) { .card { background: #171c21; border-color: #2a3138; } }
.brand { display: flex; align-items: center; gap: 8px; font-weight: 700; margin-bottom: 20px; }
.logo { color: var(--accent); }
.logo-img { height: 28px; width: auto; }
h1 { font-size: 20px; margin: 0 0 16px; }
label { display: block; font-size: 14px; margin: 12px 0 4px; color: #5c6672; }
@media (prefers-color-scheme: dark) { label { color: #9aa4af; } }
input, select { width: 100%; padding: 10px 12px; border: 1px solid #dfe3e8; border-radius: 8px; font: inherit; background: transparent; color: inherit; }
@media (prefers-color-scheme: dark) { input, select { border-color: #2a3138; } }
input:focus { outline: 2px solid var(--accent); border-color: transparent; }
button { width: 100%; margin-top: 16px; padding: 10px 12px; border: 0; border-radius: 8px; background: var(--accent); color: #fff; font: inherit; font-weight: 600; cursor: pointer; }
button:hover { filter: brightness(.92); }
button:focus-visible, a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
button.secondary { background: transparent; border: 1px solid #dfe3e8; color: inherit; }
@media (prefers-color-scheme: dark) { button.secondary { border-color: #2a3138; } }
.error { background: #fdecec; color: #9d2235; border-radius: 8px; padding: 10px 12px; margin-bottom: 8px; font-size: 14px; }
.success { background: #e8f6ee; color: #1c7c46; border-radius: 8px; padding: 10px 12px; margin-bottom: 8px; font-size: 14px; }
@media (prefers-color-scheme: dark) { .error { background: #3a1d23; } .success { background: #17301f; } }
.hint { font-size: 13px; color: #5c6672; margin-top: 14px; text-align: center; }
@media (prefers-color-scheme: dark) { .hint { color: #9aa4af; } }
.hint a { color: var(--accent); }
.list { display: grid; gap: 8px; margin: 12px 0; }
.row { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 10px 12px; border: 1px solid #dfe3e8; border-radius: 8px; font-size: 14px; }
@media (prefers-color-scheme: dark) { .row { border-color: #2a3138; } }
.muted { color: #5c6672; font-size: 13px; }
@media (prefers-color-scheme: dark) { .muted { color: #9aa4af; } }
form.inline { display: contents; }
""";
}
