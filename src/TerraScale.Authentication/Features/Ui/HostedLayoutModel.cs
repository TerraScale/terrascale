using System.Text.RegularExpressions;

namespace TerraScale.Authentication.Features.Ui;

/// <summary>
/// Model for the shared hosted-page card.
/// Razor encodes every value; the accent colour is additionally restricted to <c>#rrggbb</c>
/// because it is interpolated into CSS.
/// </summary>
public sealed partial record HostedLayoutModel
{
    /// <summary>Default accent used when an instance has no valid theme colour.</summary>
    public const string DefaultAccent = "#2158e8";

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

    /// <summary>Page title, shown as <c>{title} · TerraScale</c>.</summary>
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
@font-face { font-family: "Geist"; src: url("/fonts/Geist-Variable.woff2") format("woff2"); font-weight: 100 900; font-display: swap; }
:root {
  color-scheme: light dark;
  --accent: #2158e8; /* logo blue; an instance theme colour overrides this inline */
  --link: light-dark(var(--accent), #6b93ff); /* dark text and focus: 5.9:1 on graphite */
  --canvas: light-dark(#ffffff, #151515);
  --surface: light-dark(#fafafa, #1b1b1b);
  --ink: light-dark(#0a0a0a, #f2f2f2);
  --ink-2: light-dark(#4d4d4d, #a8a8a8);
  --ink-3: light-dark(#666666, #919191);
  --line: light-dark(rgb(0 0 0 / 0.08), rgb(255 255 255 / 0.08));
  --line-strong: light-dark(rgb(0 0 0 / 0.14), rgb(255 255 255 / 0.14));
  --control-line: light-dark(rgb(0 0 0 / 0.45), rgb(255 255 255 / 0.36));
  --danger: light-dark(#c81e1e, #ff5c5c);
  --success: light-dark(#1e7c48, #6bca92);
  --font: "Geist", ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif;
  --body-size: 14px;
  --title-size: 24px;
  --radius: 6px;
  --radius-card: 14px;
}
:root[data-theme="light"] { color-scheme: light; }
:root[data-theme="dark"] { color-scheme: dark; }
* { box-sizing: border-box; }
body { position: relative; font: var(--body-size) / 1.5 var(--font); margin: 0; background: var(--canvas); color: var(--ink); }
a { color: var(--link); }
:focus-visible { outline: 2px solid var(--link); outline-offset: 2px; }
.auth-page { min-height: 100vh; min-height: 100dvh; padding: 96px 16px; display: grid; place-items: center; align-content: center; }
.card { width: min(420px, 100%); display: grid; gap: 20px; padding: 32px; background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius-card); }
.brand { display: flex; align-items: center; justify-content: center; gap: 12px; font: 600 18px / 1.5 var(--font); letter-spacing: -0.015em; color: var(--ink); }
.logo-img { height: 32px; width: auto; }
h1 { margin: 0; font-size: var(--title-size); font-weight: 600; line-height: 1.2; letter-spacing: -0.015em; text-align: center; }
form { display: grid; gap: 6px; margin: 0; }
label { font-size: 13px; font-weight: 500; }
input, select { width: 100%; height: 36px; padding: 0 12px; border: 1px solid var(--control-line); border-radius: var(--radius); font: inherit; background: var(--surface); color: inherit; }
input:focus-visible, select:focus-visible { outline-offset: -1px; border-color: transparent; }
button { width: 100%; height: 36px; margin-top: 10px; padding: 0 12px; border: 1px solid transparent; border-radius: var(--radius); background: var(--accent); color: #fff; font: inherit; font-weight: 500; cursor: pointer; }
button:hover { background: color-mix(in oklab, var(--accent), #000 12%); }
button.secondary { background: transparent; border-color: var(--control-line); color: var(--ink); }
.error, .success { margin: 0; padding: 10px 12px; border: 1px solid; border-radius: var(--radius); font-size: 13px; }
.error { color: var(--danger); border-color: var(--danger); background: color-mix(in oklab, var(--danger) 12%, transparent); }
.success { color: var(--success); border-color: var(--success); background: color-mix(in oklab, var(--success) 12%, transparent); }
.hint { margin: 0; font-size: 12px; color: var(--ink-3); text-align: center; }
.hint a { color: var(--link); }
.muted { margin: 0; font-size: 13px; color: var(--ink-2); text-align: center; }
.list { display: grid; gap: 8px; }
.row { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 10px 12px; border: 1px solid var(--line-strong); border-radius: var(--radius); font-size: 13px; }
form.inline { display: contents; }
.auth-foot { position: absolute; left: 0; right: 0; bottom: 0; height: 96px; display: grid; justify-items: center; align-items: center; padding: 0 16px; }
.auth-powered { margin: 0; font-size: 12px; color: var(--ink-3); }
.auth-powered b { font-weight: 500; color: var(--ink-2); }
.theme-picker { width: auto; height: 28px; padding: 0 8px; font-size: 12px; }
@media (max-width: 640px) { .card { padding: 24px; } }
""";

    private static string ReadTokens()
    {
        using var stream = typeof(HostedLayoutModel).Assembly.GetManifestResourceStream("assets/ui/tokens.css")
            ?? throw new InvalidOperationException("assets/ui/tokens.css is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
