namespace TerraBase.Testing;

public static class CacheControlAssert
{
    public static async Task EqualAsync(string? actual, string? expected = "private, no-store") =>
        await Assert.That(Directives(actual)).IsEquivalentTo(Directives(expected));

    private static string[] Directives(string? value) =>
        [.. (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(directive => directive.ToLowerInvariant()).Distinct(StringComparer.Ordinal)];
}
