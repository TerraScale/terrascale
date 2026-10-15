namespace TerraBase.Testing;

/// <summary>Compares Cache-Control as a set of directives, independent of wire or HttpClient ordering.</summary>
public static class CacheControlAssert
{
    /// <summary>Requires exactly the expected directives after splitting, trimming and ignoring order.</summary>
    public static async Task EqualAsync(string? actual, string? expected = "private, no-store") =>
        await Assert.That(Directives(actual)).IsEquivalentTo(Directives(expected));

    private static string[] Directives(string? value) =>
        [.. (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(directive => directive.ToLowerInvariant()).Distinct(StringComparer.Ordinal)];
}
