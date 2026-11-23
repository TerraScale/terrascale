using System.Globalization;
using Microsoft.AspNetCore.Html;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Platform.Clients;

namespace TerraScale.Dashboard.Features.FileStorage;

internal static class FileStorageFormat
{
    public static DateTimeOffset? Created(string id) => Ulid.TryParse(id, out var ulid) ? ulid.Time : null;

    public static TerraBaseFileRules Defaults() => new("/userId", new(true, false, []), new(true, false, []));

    public static IReadOnlyList<TerraBaseStoredFile> Filtered(TerraBaseStoredFilePage page, string prefix)
    {
        return prefix.Length == 0 ? page.Items : [.. page.Items.Where(file => file.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase))];
    }

    public static IHtmlContent Time(DateTimeOffset? at, DateTimeOffset now) => at is { } value
        ? new HtmlString("<time datetime=\"" + value.ToString("O", CultureInfo.InvariantCulture) + "\" title=\"" + BackupHealth.Utc(value) + "\">" + BackupHealth.Relative(value, now) + "</time>")
        : new HtmlString("<span class=\"ts-meta\">—</span>");

    public static string Grants(TerraBaseFilePolicy policy)
    {
        string[] grants = [.. policy.Owner ? ["File owner"] : Array.Empty<string>(), .. policy.Public ? ["Authenticated users"] : Array.Empty<string>(), .. policy.Roles];
        return grants.Length == 0 ? "Project members" : string.Join(", ", grants);
    }

    public static (string Icon, string Kind) Kind(string contentType) => contentType.ToLowerInvariant() switch
    {
        "application/json" or "application/ld+json" or "application/xml" or "text/xml" or "application/javascript" or "text/javascript" or "text/css" or "text/html" => ("FileJson", "code"),
        "text/csv" or "text/tab-separated-values" or "application/vnd.ms-excel" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" or "application/x-sqlite3" or "application/vnd.sqlite3" => ("Table", "data"),
        "application/zip" or "application/gzip" or "application/x-gzip" or "application/x-tar" or "application/x-7z-compressed" or "application/vnd.rar" or "application/zstd" => ("Archive", "archive"),
        var type when type.StartsWith("image/", StringComparison.Ordinal) => ("Image", "image"),
        var type when type.StartsWith("video/", StringComparison.Ordinal) || type.StartsWith("audio/", StringComparison.Ordinal) => ("Play", "media"),
        _ => ("FileText", "file")
    };
}
