using Microsoft.AspNetCore.Html;

namespace TerraScale.Dashboard.Common.UI
{
    /// <summary>Shared navigation and accessible hint for links leaving the dashboard.</summary>
    public static class ExternalLink
    {
        public static HtmlString Attributes { get; } = new("target=\"_blank\" rel=\"noopener noreferrer\"");
        public static HtmlString Hint { get; } = new("<span class=\"ts-sr-only\"> (opens in a new tab)</span>");
    }
}
