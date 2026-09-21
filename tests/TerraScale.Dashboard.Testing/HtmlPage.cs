using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.Testing;

/// <summary>A fetched page: status plus lightweight HTML queries. The query
/// helpers are deliberately dependency-free regexes over the dashboard's own
/// markup — enough for assertions on tags, classes, and text without pulling
/// a DOM library into the offline CI restore.</summary>
public sealed partial class HtmlPage
{
    public HtmlPage(int statusCode, string html)
    {
        StatusCode = statusCode;
        Html = html;
    }

    public int StatusCode { get; }

    public string Html { get; }

    public string Title => MatchGroup(Html, "<title[^>]*>(.*?)</title>", 1)?.Trim() ?? "";

    /// <summary>The page's shell h1, or the first h2 when the route has no
    /// nav-backed title (v1 parity for sub-pages).</summary>
    public string Heading =>
        FirstTagText("h1") ?? FirstTagText("h2") ?? "";

    public IReadOnlyList<string> NavItems =>
        [.. Regex.Matches(Html, """class="ts-nav-link"[^>]*>.*?<span>(?<label>[^<]+)</span>""", RegexOptions.Singleline)
            .Select(m => m.Groups["label"].Value.Trim())];

    public bool Contains(string text) => Html.Contains(text, StringComparison.Ordinal);

    /// <summary>Visible text: tags stripped, whitespace collapsed.</summary>
    public string Text { get; private set; } = "";

    public static HtmlPage Capture(int statusCode, string html)
    {
        var text = Regex.Replace(html, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return new HtmlPage(statusCode, html) { Text = text };
    }

    public string? FirstTagText(string tag) => MatchGroup(Html, $"<{tag}[^>]*>(.*?)</{tag}>", 1)?.Trim();


    /// <summary>Every button on the page: (label, opens dialog id or null,
    /// submit button).</summary>
    public IReadOnlyList<(string Label, string? OpensDialog, bool Submit)> Buttons =>
        [.. Regex.Matches(Html, """<button\b([^>]*)>(.*?)</button>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(m =>
            {
                var attributes = m.Groups[1].Value;
                var label = Regex.Replace(m.Groups[2].Value, "<[^>]+>", "").Trim();
                var opens = Regex.Match(attributes, """data-open-dialog=["']([^"']+)["']""").Groups[1].Value;
                return (label, opens.Length is 0 ? null : opens, Regex.IsMatch(attributes, """type=["']submit["']""", RegexOptions.IgnoreCase) || !Regex.IsMatch(attributes, """\btype=["']""", RegexOptions.IgnoreCase));
            })
            .Where(b => b.Item1.Length > 0)];

    /// <summary>Value of the one-time credential secret dialog, when present.</summary>
    public string? SecretCode =>
        MatchGroup(Html, """<code class="ts-secret-code"[^>]*>(.*?)</code>""", 1)?.Trim();

    public IReadOnlyList<string> TagTexts(string tag) =>
        [.. Regex.Matches(Html, $"<{tag}[^>]*>(.*?)</{tag}>", RegexOptions.Singleline)
            .Select(m => Regex.Replace(m.Groups[1].Value, "<[^>]+>", "").Trim())
            .Where(t => t.Length > 0)];

    private static string? MatchGroup(string input, string pattern, int group) =>
        Regex.Match(input, pattern, RegexOptions.Singleline).Groups[group].Value is { Length: > 0 } value ? value : null;
}
