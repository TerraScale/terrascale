using System.Globalization;

namespace TerraScale.DotNet;

/// <summary>ts-auth route construction helpers. Path segments are data-bound
/// from caller input, so every segment is escaped; ids like <c>usr_...</c> are
/// plain but slugs, domains, and template names are caller-chosen.</summary>
internal static class TerraScaleAuthPaths
{
    public static string Escape(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        // Dot segments are never valid ids, names, or domains; keeping them
        // would let a caller mutate the route (Uri collapses ".." up a level).
        if (trimmed is "." or "..")
        {
            throw new ArgumentException("Path segments cannot be \".\" or \"..\".", nameof(value));
        }

        return Uri.EscapeDataString(trimmed);
    }

    public static string Query(int? limit, string? startingAfter)
    {
        if (limit is null && startingAfter is null)
        {
            return string.Empty;
        }

        var query = "?";
        if (limit is { } limitValue)
        {
            query += "limit=" + limitValue.ToString(CultureInfo.InvariantCulture);
            if (startingAfter is not null)
            {
                query += "&";
            }
        }

        if (startingAfter is not null)
        {
            query += "starting_after=" + Uri.EscapeDataString(startingAfter);
        }

        return query;
    }
}
