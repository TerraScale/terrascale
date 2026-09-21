using System.Text;
using Microsoft.AspNetCore.Html;

namespace TerraScale.Dashboard;

/// <summary>Document skeleton for standalone flow pages (no dashboard shell):
/// reset password, approvals, consent, invitations, checkout, portal, onboarding.
/// Same head as the shell pages: terrascale.css, datastar, brand favicon.</summary>
public static class FlowHtml
{
    public static HtmlString Document(string title)
    {
        var builder = new StringBuilder("""
            <!DOCTYPE html>
            <html lang="en" data-theme="dark">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
            """);
        builder.AppendLine($"    <title>{Escape(title)} · TerraScale</title>");
        builder.AppendLine("""
                <link rel="icon" href="/brand/logo-icon-blue.svg" type="image/svg+xml" />
                <link rel="stylesheet" href="/css/terrascale.css" />
                <script type="module" src="/js/datastar.js"></script>
            </head>
            <body>
            """);
        return new HtmlString(builder.ToString());
    }

    public static HtmlString End() => new("</body>\n</html>\n");

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
