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

    /// <summary>
    /// The shared hosted-page stylesheet. Colours, type and shape are <c>--ts-*</c> tokens (the dashboard's names), light
    /// first and dark under <c>prefers-color-scheme</c>; the rules below them only reference tokens.
    /// </summary>
    public const string PageCss = """
@font-face { font-family: "Oxanium"; src: url(/fonts/oxanium-latin-wght-normal.woff2) format("woff2"); font-weight: 200 800; font-display: swap; }
@font-face { font-family: "JetBrains Mono"; src: url(/fonts/jetbrains-mono-latin-wght-normal.woff2) format("woff2"); font-weight: 100 800; font-display: swap; }
:root {
  color-scheme: light dark;
  --ts-canvas: #f3f5f9;
  --ts-surface: #ffffff;
  --ts-line: #e2e7ef;
  --ts-control-line: #848ea0;
  --ts-control-line-hover: #5b6474;
  --ts-ink: #0b1220;
  --ts-ink-2: #5b6474;
  --ts-accent: #2158e8;
  --ts-accent-soft: #e9effd;
  --ts-accent-fill: #2158e8;
  --ts-accent-fill-hover: #1a47bf;
  --ts-on-accent: #ffffff;
  --ts-link: #2158e8;
  --ts-danger: #b42318;
  --ts-danger-soft: #fef1f1;
  --ts-success: #11713f;
  --ts-success-soft: #ebf7f0;
  --ts-font-sans: "Oxanium", ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
  --ts-font-mono: "JetBrains Mono", ui-monospace, "SFMono-Regular", Menlo, Consolas, monospace;
  --ts-fs-xs: 12px;
  --ts-fs-sm: 13px;
  --ts-fs-md: 14px;
  --ts-fs-lg: 16px;
  --ts-fs-xl: 20px;
  --ts-s1: 4px;
  --ts-s2: 8px;
  --ts-s3: 12px;
  --ts-s4: 16px;
  --ts-s6: 24px;
  --ts-s8: 32px;
  --ts-radius: 6px;
  --ts-radius-lg: 10px;
  --ts-control-h: 40px;
  --ts-fast: 120ms;
  --ts-ease: cubic-bezier(0.2, 0.8, 0.2, 1);
}
@media (prefers-color-scheme: dark) {
  :root {
    --ts-canvas: #0a0c12;
    --ts-surface: #11141c;
    --ts-line: #232838;
    --ts-control-line: #5e677b;
    --ts-control-line-hover: #7a8397;
    --ts-ink: #eef1f7;
    --ts-ink-2: #a7afbf;
    --ts-accent: #3b82f6;
    --ts-accent-soft: #15213a;
    --ts-accent-fill: #2563eb;
    --ts-accent-fill-hover: #1d4ed8;
    --ts-link: #6ea8ff;
    --ts-danger: #fca5a5;
    --ts-danger-soft: #2a1519;
    --ts-success: #34d399;
    --ts-success-soft: #0f2a20;
  }
}
* { box-sizing: border-box; }
body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: var(--ts-s6) var(--ts-s4); background: var(--ts-canvas); color: var(--ts-ink); font: var(--ts-fs-md)/1.5 var(--ts-font-sans); -webkit-text-size-adjust: 100%; }
.shell { width: min(400px, 100%); }
.brand { margin: 0 0 var(--ts-s6); }
.logo, .logo-img { display: block; height: 36px; width: auto; }
.card { background: var(--ts-surface); border: 1px solid var(--ts-line); border-radius: var(--ts-radius-lg); padding: var(--ts-s8); }
.icon { width: 16px; height: 16px; flex: none; }
.badge { display: grid; place-items: center; width: 40px; height: 40px; margin-bottom: var(--ts-s4); border-radius: var(--ts-radius-lg); background: var(--ts-accent-soft); color: var(--ts-accent); }
.badge .icon { width: 20px; height: 20px; }
h1 { font-size: var(--ts-fs-xl); line-height: 1.3; font-weight: 600; margin: 0 0 var(--ts-s2); }
p { margin: 0 0 var(--ts-s4); }
label { display: block; font-size: var(--ts-fs-sm); font-weight: 500; margin: var(--ts-s4) 0 var(--ts-s1); color: var(--ts-ink-2); }
input { width: 100%; height: var(--ts-control-h); padding: 0 var(--ts-s3); border: 1px solid var(--ts-control-line); border-radius: var(--ts-radius); font: inherit; font-size: var(--ts-fs-lg); background: transparent; color: inherit; transition: border-color var(--ts-fast) var(--ts-ease); }
input:hover { border-color: var(--ts-control-line-hover); }
input:focus { outline: 2px solid var(--ts-accent); outline-offset: -1px; border-color: var(--ts-accent); }
input.code { height: 52px; font-family: var(--ts-font-mono); font-size: 24px; letter-spacing: 0.5em; text-align: center; padding-left: calc(var(--ts-s3) + 0.5em); }
button { width: 100%; height: var(--ts-control-h); margin-top: var(--ts-s4); padding: 0 var(--ts-s4); border: 0; border-radius: var(--ts-radius); background: var(--ts-accent-fill); color: var(--ts-on-accent); font: inherit; font-weight: 600; cursor: pointer; transition: background-color var(--ts-fast) var(--ts-ease); }
button:hover { background: var(--ts-accent-fill-hover); }
button:focus-visible, a:focus-visible { outline: 2px solid var(--ts-accent); outline-offset: 2px; }
a { color: var(--ts-link); }
.actions { display: flex; justify-content: space-between; align-items: center; gap: var(--ts-s3); margin-top: var(--ts-s4); font-size: var(--ts-fs-sm); }
.actions form { display: contents; }
.back { display: inline-flex; align-items: center; gap: var(--ts-s1); color: var(--ts-ink-2); text-decoration: none; }
.back:hover { color: var(--ts-ink); }
button.link { width: auto; height: auto; margin: 0; padding: 0; background: none; color: var(--ts-link); font-weight: 500; }
button.link:hover { background: none; text-decoration: underline; }
.error, .success { display: flex; gap: var(--ts-s2); align-items: flex-start; border-radius: var(--ts-radius); padding: var(--ts-s3); margin: 0 0 var(--ts-s4); font-size: var(--ts-fs-md); }
.error .icon, .success .icon { margin-top: 2px; }
.error { background: var(--ts-danger-soft); color: var(--ts-danger); }
.success { background: var(--ts-success-soft); color: var(--ts-success); }
.muted { color: var(--ts-ink-2); }
.muted strong { color: var(--ts-ink); font-weight: 600; }
.detail { font-family: var(--ts-font-mono); font-size: var(--ts-fs-xs); color: var(--ts-ink-2); }
.foot { margin-top: var(--ts-s6); font-size: var(--ts-fs-xs); color: var(--ts-ink-2); }
.foot a { color: inherit; }
""";
}
