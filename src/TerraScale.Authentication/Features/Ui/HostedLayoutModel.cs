using System.Text.RegularExpressions;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Ui;

public sealed partial record HostedLayoutModel
{
    public const string DefaultAccent = "#2158e8";

    public const string DefaultName = "TerraScale";

    public static readonly IReadOnlyList<(string Label, string Url)> DefaultFooter =
    [
        ("Status", "https://status.terrascale.tech"),
        ("Docs", "https://docs.terrascale.tech"),
        ("Privacy", "https://docs.terrascale.tech/legal/privacy"),
        ("Terms", "https://docs.terrascale.tech/legal/terms"),
    ];

    public HostedLayoutModel(string title, string nonce, string? themeColor = null, string? logoUrl = null, string? name = null, int? titleSize = null,
        IReadOnlyList<(string Label, string Url)>? footer = null, string? defaultTheme = null, string? tenant = null,
        string? backgroundColor = null, string? textColor = null, string? locale = null)
    {
        this.Title = title;
        this.Nonce = nonce;
        this.Locale = locale == AuthEmails.Portuguese ? AuthEmails.Portuguese : AuthEmails.English;
        this.LogoUrl = HttpsUrl(logoUrl) ? logoUrl : null;
        this.Name = string.IsNullOrWhiteSpace(name) ? DefaultName : name;
        this.AccentColor = ValidColor(themeColor);
        this.TitleSize = titleSize is >= 20 and <= 40 ? titleSize : null;
        var copy = this.Copy;
        string[] defaultLabels = [copy.FooterStatus, copy.FooterDocs, copy.FooterPrivacy, copy.FooterTerms];
        this.Footer = footer is null
            ? [.. DefaultFooter.Select((link, index) => (defaultLabels[index], link.Url))]
            : [.. footer.Where(link => !string.IsNullOrWhiteSpace(link.Label) && HttpsUrl(link.Url)).Take(5)];
        this.DefaultTheme = defaultTheme is "light" or "dark" ? defaultTheme : "system";
        this.ThemeKey = "ts-auth-theme:" + (tenant ?? DefaultName);
        this.BackgroundColor = ValidColor(backgroundColor);
        this.TextColor = ValidColor(textColor);
        this.Palette = HostedPalette.Css(this.AccentColor, this.BackgroundColor, this.TextColor);
    }

    public static HostedLayoutModel Themed(string title, string nonce, HostedTheme theme, string tenant, string? locale)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return new(title, nonce, theme.ThemeColor, theme.LogoUrl, theme.Name, theme.TitleSize, theme.FooterLinks?.Select(link => (link.Label, link.Url)).ToArray(),
            theme.DefaultTheme, tenant, theme.BackgroundColor, theme.TextColor, locale);
    }

    public string Title { get; }

    public string Nonce { get; }

    public bool ContinueOnly { get; init; }

    public string Locale { get; }

    public HostedPageCopy Copy => HostedCopies.For(this.Locale);

    public string? AccentColor { get; }

    public string? LogoUrl { get; }

    public string Name { get; }

    public int? TitleSize { get; }

    public IReadOnlyList<(string Label, string Url)> Footer { get; }

    public string DefaultTheme { get; }

    public string ThemeKey { get; }

    public string? BackgroundColor { get; }

    public string? TextColor { get; }

    public string Palette { get; }

    public Uri? PublicOrigin { get; init; }

    public bool IsExternalFooterUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var destination)
        && (this.PublicOrigin is not { } origin || destination.Scheme != origin.Scheme || destination.Port != origin.Port
            || !string.Equals(destination.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase));

    private static string? ValidColor(string? value) => value is not null && HexColor().IsMatch(value) ? value : null;

    private static bool HttpsUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);

    [GeneratedRegex(@"\A#[0-9A-Fa-f]{6}\z")]
    private static partial Regex HexColor();

    public const string PageCss = """
@font-face { font-family: "Geist"; src: url("/fonts/Geist-Variable.woff2") format("woff2"); font-weight: 100 900; font-display: swap; }
@font-face { font-family: "Geist Mono"; src: url("/fonts/GeistMono-Variable.woff2") format("woff2"); font-weight: 100 900; font-display: swap; }
:root {
  color-scheme: light dark;
  --accent: #2158e8;
  --on-accent: #ffffff;
  --accent-hover: #1a4bd0; /* logo blue; an instance theme colour overrides this inline */
  --link: light-dark(#1f52d9, #6b93ff); /* dark text and focus: 5.9:1 on graphite */
  --canvas: light-dark(#ffffff, #151515);
  --surface: light-dark(#fafafa, #1b1b1b);
  --surface-2: light-dark(#f4f4f5, #222222);
  --ink: light-dark(#0a0a0a, #f2f2f2);
  --ink-2: light-dark(#4d4d4d, #a8a8a8);
  --ink-3: light-dark(#666666, #919191);
  --line: light-dark(rgb(0 0 0 / 0.08), rgb(255 255 255 / 0.08));
  --line-strong: light-dark(rgb(0 0 0 / 0.14), rgb(255 255 255 / 0.14));
  --control-line: light-dark(rgb(0 0 0 / 0.45), rgb(255 255 255 / 0.36));
  --danger: light-dark(#c81e1e, #ff5c5c);
  --warning: light-dark(#a35200, #ffa726);
  --success: light-dark(#1e7c48, #6bca92);
  --font: "Geist", ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif;
  --mono: "Geist Mono", ui-monospace, "SFMono-Regular", Menlo, Consolas, monospace;
  --body-size: 14px;
  --title-size: 24px;
  --radius: 6px;
  --radius-card: 14px;
}
:root[data-theme="light"] { color-scheme: light; }
:root[data-theme="dark"] { color-scheme: dark; }
* { box-sizing: border-box; }
body { position: relative; font: var(--body-size) / 1.5 var(--font); margin: 0; background: var(--page-background, var(--canvas)); color: var(--ink); }
a { color: var(--link); text-decoration: none; }
a:hover { text-decoration: underline; }
:focus-visible { outline: 2px solid var(--link); outline-offset: 2px; }
p { margin: 0; }
.auth-page { min-height: 100vh; min-height: 100dvh; padding: 96px 16px; display: grid; grid-template-columns: minmax(0, 1fr); place-items: center; align-content: center; }
.card { width: min(420px, 100%); min-width: 0; display: grid; grid-template-columns: minmax(0, 1fr); gap: 20px; padding: 32px; background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius-card); }
.brand { display: flex; align-items: center; justify-content: center; gap: 12px; min-width: 0; font: 600 18px / 1.5 var(--font); letter-spacing: -0.015em; color: var(--ink); }
.brand span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.logo-img { width: 32px; height: 32px; object-fit: contain; flex: none; }
.head { display: grid; gap: 4px; text-align: center; }
h1 { margin: 0; font-size: var(--title-size); font-weight: 600; line-height: 1.2; letter-spacing: -0.015em; }
.sub { color: var(--ink-2); overflow-wrap: anywhere; }
.sub strong { color: var(--ink); font-weight: 500; }
.icon { width: 16px; height: 16px; flex: none; fill: none; stroke: currentColor; stroke-width: 1.5; stroke-linecap: round; stroke-linejoin: round; }
.icon-sm { width: 14px; height: 14px; }
form { display: grid; grid-template-columns: minmax(0, 1fr); gap: 16px; margin: 0; }
.field { display: grid; gap: 6px; min-width: 0; }
label { display: flex; justify-content: space-between; gap: 8px; font-size: 13px; font-weight: 500; }
label a { font-weight: 400; }
.hint { font-size: 12px; color: var(--ink-3); }
input, select { width: 100%; height: 36px; padding: 0 12px; border: 1px solid var(--control-line); border-radius: var(--radius); font: inherit; background: var(--surface); color: inherit; }
input::placeholder { color: var(--ink-3); }
input:focus-visible, select:focus-visible { outline-offset: -1px; border-color: transparent; }
input.code { height: 52px; font: 500 24px / 1 var(--mono); letter-spacing: 0.35em; text-align: center; caret-color: var(--accent); }
.chip { display: flex; align-items: center; gap: 8px; height: 36px; padding: 0 4px 0 12px; border: 1px solid var(--line-strong); border-radius: var(--radius); background: var(--canvas); }
.chip > .icon { color: var(--ink-3); }
.chip span { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.chip a { padding: 0 8px; font-size: 13px; }
button, .btn { display: inline-flex; align-items: center; justify-content: center; gap: 8px; width: 100%; height: 36px; padding: 0 12px; border: 1px solid transparent; border-radius: var(--radius); background: var(--accent); color: var(--on-accent); font: inherit; font-weight: 500; text-decoration: none; cursor: pointer; }
button:hover, .btn:hover { background: var(--accent-hover); text-decoration: none; }
button.secondary, .btn.secondary { background: var(--surface); border-color: var(--control-line); color: var(--ink); }
button.secondary:hover, .btn.secondary:hover { background: var(--surface-2); }
button.danger { background: light-dark(#c81e1e, #d92626); }
.actions { display: grid; gap: 8px; }
.alert { --state: var(--danger); display: flex; gap: 12px; padding: 12px 16px; border: 1px solid var(--state); border-radius: var(--radius); background: color-mix(in oklab, var(--state) 12%, transparent); }
.impersonation-banner { position: sticky; top: 0; z-index: 1; }
.impersonation-banner strong { overflow-wrap: anywhere; }
.alert[data-state="limited"] { --state: var(--warning); }
.alert[data-state="success"] { --state: var(--success); }
.alert > .icon { margin-top: 2px; color: var(--state); }
.alert > div { min-width: 0; }
.alert strong { display: block; font-weight: 500; color: var(--state); }
.alert p { font-size: 13px; color: var(--ink-2); overflow-wrap: anywhere; }
.row { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; font-size: 13px; }
.row a { display: inline-flex; align-items: center; gap: 6px; }
.alt { text-align: center; font-size: 13px; color: var(--ink-2); }
.device-code { padding: 16px; border: 1px solid var(--line-strong); border-radius: 10px; background: var(--canvas); font: 500 28px / 1 var(--mono); letter-spacing: 0.12em; text-align: center; }
ul.scopes { display: grid; gap: 6px; margin: 0; padding: 0; list-style: none; font-size: 13px; color: var(--ink-2); }
ul.scopes li { padding-left: 18px; position: relative; }
ul.scopes li::before { content: ""; position: absolute; left: 2px; top: 0.55em; width: 6px; height: 6px; border-radius: 50%; background: var(--ink-3); }
.facts { display: grid; gap: 8px; margin: 0; font-size: 13px; }
.facts div { display: flex; justify-content: space-between; gap: 12px; }
.facts dt { color: var(--ink-3); }
.facts dd { margin: 0; min-width: 0; overflow-wrap: anywhere; text-align: right; }
.auth-foot { position: absolute; left: 0; right: 0; bottom: 0; min-height: 96px; display: grid; align-content: center; justify-items: center; gap: 8px; padding: 8px 16px; font-size: 12px; color: var(--ink); }
.foot-links { display: flex; flex-wrap: wrap; justify-content: center; gap: 16px; }
.foot-links a { color: var(--ink-3); }
.foot-row { display: flex; align-items: center; justify-content: center; gap: 12px; }
.auth-powered { color: var(--ink-3); }
.auth-powered b { font-weight: 500; color: var(--ink-2); }
.theme-picker { width: auto; height: 28px; padding: 0 8px; font-size: 12px; background: var(--page-background, var(--canvas)); }
.password-control { display: flex; align-items: center; border: 1px solid var(--control-line); border-radius: var(--radius); background: var(--surface); }
.password-control input { border: 0; background: transparent; min-width: 0; }
.password-control:focus-within { outline: 2px solid var(--link); outline-offset: -1px; border-color: transparent; }
.password-control input:focus-visible { outline: none; }
.password-toggle { flex: none; width: 30px; height: 30px; margin-right: 2px; padding: 0; color: var(--ink-3); background: transparent; }
.password-toggle:hover { background: var(--surface-2); }
.otp { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)) 8px repeat(3, minmax(0, 1fr)); gap: 8px; }
.otp input { height: 52px; padding: 0; font: 500 24px / 1 var(--mono); text-align: center; caret-color: var(--accent); }
.otp input:nth-child(4) { grid-column: 5; }
.otp input:not(:placeholder-shown) { background: var(--surface-2); }
button.is-loading, .btn.is-loading { color: transparent; pointer-events: none; }
button.is-loading > *, .btn.is-loading > * { visibility: hidden; }
button.is-loading::after, .btn.is-loading::after { content: ""; width: 1em; height: 1em; border: 2px solid color-mix(in oklab, currentColor 35%, transparent); border-top-color: currentColor; border-radius: 50%; animation: ts-spin 0.7s linear infinite; }
.continue-action { width: min(100%, 360px); }
.delayed-continue { animation: ts-continue 1.5s step-end; }
@keyframes ts-continue { from { visibility: hidden; } to { visibility: visible; } }
@keyframes ts-spin { to { transform: rotate(1turn); } }
@media (max-width: 640px) { .auth-page { padding-left: 12px; padding-right: 12px; } .card { padding: 24px; } input.code { font-size: 20px; } .otp { gap: 6px; } .otp input { height: 48px; font-size: 20px; } }
@media (prefers-reduced-motion: reduce) { * { transition: none !important; } button.is-loading::after, .btn.is-loading::after { animation: none; opacity: 0.6; } }
""";
}
