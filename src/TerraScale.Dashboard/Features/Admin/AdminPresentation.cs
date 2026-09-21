using System.Globalization;
using Microsoft.AspNetCore.Html;

namespace TerraScale.Dashboard.Features.Admin;

/// <summary>Shared presentation helpers for the admin pages — 1:1 port of
/// src/features/admin/admin-presentation.tsx (AdminError, AdminFilter,
/// AdminStat, AdminTable shell, filterAdminRows, adminTimestamp).</summary>
public static class AdminPresentation
{
    public static HtmlString Stat(string label, string value) => new(
        $"""
        <div class="ts-admin-stat">
            <div class="ts-admin-stat-label">{label}</div>
            <div class="ts-admin-stat-value">{value}</div>
        </div>
        """);

    public static HtmlString Filter(string action, string label, string query) => new(
        $"""
        <div class="ts-panel ts-admin-filter-panel">
            <form class="ts-admin-filter-form" action="{action}" method="get" role="search">
                <label class="ts-sr-only" for="admin-filter">Filter {label.ToLowerInvariant()}</label>
                {Icons.Search(16).Value}
                <input class="ts-input ts-admin-filter-input" id="admin-filter" name="q" type="search" value="{Escape(query)}" placeholder="Filter {label.ToLowerInvariant()}" />
            </form>
        </div>
        """);

    /// <summary>Opens the admin table panel: page renders thead/rows then closes.</summary>
    public static HtmlString TableStart(string title) => new(
        $"""
        <section class="ts-panel">
            <div class="ts-panel-header">
                <h2 class="ts-panel-title">{title}</h2>
                <p class="ts-panel-subtitle">Owner-visible data returned by the admin API</p>
            </div>
            <div class="ts-admin-table-body">
        """);

    public static HtmlString TableEnd => new("</div></section>");

    public static HtmlString TableEmpty(string title) => new(
        $"""
        <div class="ts-empty-card">
            <span class="ts-empty-icon-box">{Icons.SearchX(16).Value}</span>
            <span class="ts-empty-card-title">No admin {title.ToLowerInvariant()}</span>
            <span class="ts-empty-card-desc">No owner-visible records matched this request.</span>
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

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;");
}
