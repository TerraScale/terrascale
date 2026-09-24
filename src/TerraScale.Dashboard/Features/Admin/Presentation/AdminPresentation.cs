using System.Globalization;
using Microsoft.AspNetCore.Html;

namespace TerraScale.Dashboard.Features.Admin.Presentation;

/// <summary>Shared presentation helpers for the admin pages — 1:1 port of
/// src/features/admin/admin-presentation.tsx (AdminError, AdminFilter,
/// AdminStat, AdminTable shell, filterAdminRows, adminTimestamp).</summary>
public static class AdminPresentation
{
    public const int PageSize = 10;

    public sealed record Page<T>(IReadOnlyList<T> Rows, int Number, int TotalPages, int TotalRows);

    public static HtmlString Stat(string label, string value, string icon = "Activity") => new(
        $"""
        <div>
            <dt>{Icon(icon, 14).Value} {label}</dt>
            <dd><span class="ts-kpi-value">{value}</span></dd>
        </div>
        """);

    public static HtmlString Filter(string action, string label, string query, string? tab = null) => new(
        $"""
        <div class="ts-toolbar ts-admin-toolbar">
            <form class="ts-filters ts-admin-filter-form" action="{action}" method="get" role="search">
                {(tab is { Length: > 0 } ? $"<input type=\"hidden\" name=\"tab\" value=\"{Escape(tab)}\" />" : "")}
                <label class="ts-sr-only" for="admin-filter">Filter {label.ToLowerInvariant()}</label>
                <span class="ts-admin-filter-icon" aria-hidden="true">{Icons.Search(16).Value}</span>
                <input class="ts-input ts-admin-filter-input" id="admin-filter" name="q" type="search" value="{Escape(query)}" placeholder="Filter {label.ToLowerInvariant()}" />
                {(query.Trim().Length is 0 ? "" : $"<a class=\"ts-btn ts-btn-ghost\" href=\"{action}{(tab is { Length: > 0 } ? $"?tab={Escape(tab)}" : "") }\">Clear</a>")}
            </form>
        </div>
        """);

    /// <summary>Opens a flat admin section: page renders the table then closes.</summary>
    public static HtmlString TableStart(string title) => new(
        $"""
        <section class="ts-admin-section" aria-labelledby="{Slug(title)}-title">
            <div class="ts-section-head">
                <div>
                    <h2 class="ts-panel-title" id="{Slug(title)}-title">{title}</h2>
                    <p class="ts-meta">Owner-visible data returned by the admin API</p>
                </div>
            </div>
        """);

    public static HtmlString TableEnd => new("</section>");

    public static HtmlString TableEmpty(string title) => new(
        $"""
        <div class="ts-empty">
            <span class="ts-empty-icon">{Icons.SearchX(16).Value}</span>
            <span class="ts-empty-title">No admin {title.ToLowerInvariant()}</span>
            <span class="ts-meta">No owner-visible records matched this request.</span>
        </div>
        """);

    /// <summary>adminTimestamp: medium date + short time, UTC — or "-" when unparseable.</summary>
    public static string Timestamp(string value)
    {
        if (value.Length is 0 || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return value.Length is 0 ? "-" : value;
        }

        return date.UtcDateTime.ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<T> FilterRows<T>(IReadOnlyList<T> rows, string query, Func<T, string> text)
    {
        var term = query.Trim().ToLowerInvariant();
        return term.Length is 0 ? rows : [.. rows.Where(row => text(row).ToLowerInvariant().Contains(term))];
    }

    public static Page<T> Paginate<T>(IReadOnlyList<T> rows, string? requestedPage)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)PageSize));
        var page = int.TryParse(requestedPage, out var parsed) ? Math.Clamp(parsed, 1, totalPages) : 1;
        return new(
            rows.Skip((page - 1) * PageSize).Take(PageSize).ToArray(),
            page,
            totalPages,
            rows.Count);
    }

    public static HtmlString Pager(string action, int page, int totalPages, int totalRows, string query = "", string? tab = null)
    {
        if (totalPages <= 1)
        {
            return new("");
        }

        var first = ((page - 1) * PageSize) + 1;
        var last = Math.Min(page * PageSize, totalRows);
        var links = new System.Text.StringBuilder();
        if (page > 1)
        {
            links.Append($"<a class=\"ts-btn ts-btn-ghost ts-pager-page\" href=\"{Href(action, page - 1, query, tab)}\">Previous</a>");
        }

        for (var number = 1; number <= totalPages; number++)
        {
            var current = number == page ? " aria-current=\"page\"" : "";
            links.Append($"<a class=\"ts-btn ts-btn-ghost ts-pager-page\" href=\"{Href(action, number, query, tab)}\"{current}>{number}</a>");
        }

        if (page < totalPages)
        {
            links.Append($"<a class=\"ts-btn ts-btn-ghost ts-pager-page\" href=\"{Href(action, page + 1, query, tab)}\">Next</a>");
        }

        return new($"<nav class=\"ts-pager\" aria-label=\"Pages\"><span class=\"ts-meta\">Showing {first}–{last} of {totalRows}</span><span class=\"ts-row\">{links}</span></nav>");
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;");

    private static string Slug(string value) =>
        "admin-" + new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Href(string action, int page, string query, string? tab)
    {
        var parameters = new List<string>();
        if (tab is { Length: > 0 })
        {
            parameters.Add($"tab={Uri.EscapeDataString(tab)}");
        }

        if (query.Trim().Length > 0)
        {
            parameters.Add($"q={Uri.EscapeDataString(query.Trim())}");
        }

        parameters.Add($"page={page}");
        return Escape($"{action}?{string.Join("&", parameters)}");
    }

    private static HtmlString Icon(string name, int size) =>
        (typeof(Icons).GetMethod(name)?.Invoke(null, [size]) as HtmlString) ?? Icons.Circle(size);
}
