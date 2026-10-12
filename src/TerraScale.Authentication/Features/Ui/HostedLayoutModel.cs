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
    /// The design tokens (<c>Assets/ui/tokens.css</c>, a verbatim copy of one of the shared TerraScale token files): every
    /// <c>--ts-*</c> colour, type and shape value for the hosted pages, and the source of the email palette.
    /// </summary>
    public static readonly string TokensCss = ReadTokens();

    /// <summary>The hosted-page stylesheet: the tokens, then <see cref="ComponentsCss"/>.</summary>
    public static readonly string PageCss = TokensCss + ComponentsCss;

    /// <summary>
    /// The sign-in card components of the shared design system (<c>.ts-auth</c>, fields, buttons, alerts), written only
    /// against the tokens. Fonts are self-hosted under <c>/fonts</c>: the CSP allows same-origin fonts only.
    /// </summary>
    public const string ComponentsCss = """
@font-face { font-family: "Geist"; src: url(/fonts/Geist-Variable.woff2) format("woff2"); font-weight: 100 900; font-display: swap; }
@font-face { font-family: "Geist Mono"; src: url(/fonts/GeistMono-Variable.woff2) format("woff2"); font-weight: 100 900; font-display: swap; }
@font-face { font-family: "Oxanium"; src: url(/fonts/oxanium-latin-wght-normal.woff2) format("woff2"); font-weight: 200 800; font-display: swap; }
*, *::before, *::after { box-sizing: border-box; }
html { -webkit-text-size-adjust: 100%; accent-color: var(--ts-accent); }
body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: var(--ts-s10) var(--ts-s4); background: var(--ts-canvas); color: var(--ts-ink); font: 400 var(--ts-fs-md) / var(--ts-lh) var(--ts-font-sans); font-feature-settings: "liga" 1, "calt" 1; -webkit-font-smoothing: antialiased; }
h1 { margin: 0; font-family: var(--ts-font-display); font-weight: var(--ts-heading-weight); letter-spacing: var(--ts-heading-tracking); line-height: var(--ts-lh-tight); color: var(--ts-ink); }
p { margin: 0; }
a { color: var(--ts-link); text-decoration: none; }
a:hover { text-decoration: underline; text-underline-offset: 3px; }
:focus-visible { outline: 2px solid var(--ts-focus); outline-offset: 2px; border-radius: var(--ts-radius-sm); }
::selection { background: var(--ts-accent-soft); }
svg.ts-icon { width: var(--ts-icon); height: var(--ts-icon); flex: none; fill: none; stroke: currentColor; stroke-width: var(--ts-icon-stroke); stroke-linecap: round; stroke-linejoin: round; }
svg.ts-icon-sm { width: var(--ts-icon-sm); height: var(--ts-icon-sm); }
@media (prefers-reduced-motion: reduce) { *, *::before, *::after { transition-duration: 0.01ms !important; } }
.ts-auth { width: min(var(--ts-form-max), 100%); padding: var(--ts-s8); border: 1px solid var(--ts-line); border-radius: var(--ts-radius-xl); background: var(--ts-surface); display: grid; gap: var(--ts-s5); }
.ts-auth-logo { display: block; height: 28px; width: auto; }
.ts-auth h1 { font-size: var(--ts-fs-2xl); }
.ts-auth-sub { color: var(--ts-ink-2); margin-top: var(--ts-s1); }
.ts-auth-sub strong { color: var(--ts-ink); font-weight: 500; }
.ts-auth form { display: grid; gap: var(--ts-s5); }
.ts-auth-app { display: inline-flex; align-items: center; gap: var(--ts-s2); font-size: var(--ts-fs-sm); color: var(--ts-ink-3); }
.ts-auth-links { display: flex; justify-content: space-between; align-items: center; gap: var(--ts-s3); }
.ts-auth-links form { display: contents; }
.ts-auth-links a { display: inline-flex; align-items: center; gap: 6px; }
.ts-link-btn { padding: 0; border: 0; background: none; color: var(--ts-link); font: inherit; cursor: pointer; }
.ts-link-btn:hover { text-decoration: underline; text-underline-offset: 3px; }
.ts-divider { display: flex; align-items: center; gap: var(--ts-s3); color: var(--ts-ink-3); font-size: var(--ts-fs-xs); }
.ts-divider::before, .ts-divider::after { content: ""; flex: 1; height: 1px; background: var(--ts-line); }
.ts-auth-foot { display: flex; justify-content: center; gap: var(--ts-s4); font-size: var(--ts-fs-xs); color: var(--ts-ink-3); }
.ts-auth-foot a { color: var(--ts-ink-3); }
.ts-field { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
.ts-field > label { font-size: var(--ts-fs-sm); font-weight: 500; color: var(--ts-ink); }
.ts-input { height: var(--ts-control-h); width: 100%; padding: 0 var(--ts-s3); border: 1px solid var(--ts-control-line); border-radius: var(--ts-radius); background: var(--ts-surface); color: var(--ts-ink); font: 400 var(--ts-fs-md) / 1 var(--ts-font-sans); transition: border-color var(--ts-fast) var(--ts-ease); }
.ts-input:hover { border-color: var(--ts-control-line-hover); }
.ts-input:focus-visible { outline: 2px solid var(--ts-focus); outline-offset: 0; border-color: transparent; }
.ts-input-code { height: var(--ts-control-h-lg); padding-left: calc(var(--ts-s3) + 0.5em); font: 500 var(--ts-fs-xl) / 1 var(--ts-font-mono); letter-spacing: 0.5em; text-align: center; }
.ts-btn { display: inline-flex; align-items: center; justify-content: center; gap: var(--ts-s2); height: var(--ts-control-h); padding: 0 var(--ts-s4); border: 1px solid transparent; border-radius: var(--ts-radius); font: 500 var(--ts-fs-md) / 1 var(--ts-font-sans); white-space: nowrap; cursor: pointer; text-decoration: none; transition: background-color var(--ts-fast) var(--ts-ease), border-color var(--ts-fast) var(--ts-ease), color var(--ts-fast) var(--ts-ease); }
.ts-btn:hover { text-decoration: none; }
.ts-btn-lg { height: var(--ts-control-h-lg); padding: 0 var(--ts-s5); }
.ts-btn-block { width: 100%; }
.ts-btn-primary { background: var(--ts-primary); color: var(--ts-on-primary); }
.ts-btn-primary:hover { background: var(--ts-primary-hover); }
.ts-alert { --state: var(--ts-info); display: flex; gap: var(--ts-s3); padding: var(--ts-s3) var(--ts-s4); border-left: 2px solid var(--state); border-radius: 0 var(--ts-radius) var(--ts-radius) 0; background: color-mix(in oklab, var(--state) var(--ts-tint-alert), transparent); color: var(--ts-ink); }
.ts-alert[role="alert"] { --state: var(--ts-danger); }
.ts-alert[role="status"] { --state: var(--ts-success); }
.ts-alert > svg { color: var(--state); margin-top: 2px; }
.ts-detail { font: 400 var(--ts-fs-xs) / var(--ts-lh) var(--ts-font-mono); color: var(--ts-ink-3); }
""";

    private static string ReadTokens()
    {
        using var stream = typeof(HostedLayoutModel).Assembly.GetManifestResourceStream("assets/ui/tokens.css")
            ?? throw new InvalidOperationException("assets/ui/tokens.css is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
