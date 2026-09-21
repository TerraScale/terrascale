using System.Globalization;

namespace TerraScale.Dashboard.Features.ApiKeys;

/// <summary>Port of formatUtcDateTime (en, UTC, medium date + short time).</summary>
public static class ApiKeyFormatting
{
    public static string UtcDateTime(string? value, string fallback = "Never")
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.UtcDateTime.ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture)
            : value;
    }
}
