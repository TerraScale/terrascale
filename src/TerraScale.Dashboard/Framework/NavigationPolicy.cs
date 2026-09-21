namespace TerraScale.Dashboard;

/// <summary>Only query-free, read-only demo pages are eligible. Live page GETs
/// still persist account state and must never run speculatively.</summary>
public static class NavigationPolicy
{
    public const string Rules = """
        <template id="ts-prefetch-rules"><script type="speculationrules">{"prefetch":[{"where":{"and":[{"selector_matches":"a[data-prefetch]"},{"href_matches":["/","/databases","/data"]}]},"eagerness":"moderate"}]}</script></template>
        """;

    public static bool IsPrefetchPath(string path) => path is "/" or "/databases" or "/data";

    public static bool IsSpeculation(HttpRequest request) =>
        request.Headers["Sec-Purpose"].ToString().Contains("prefetch", StringComparison.OrdinalIgnoreCase)
        || request.Headers["Purpose"].ToString().Contains("prefetch", StringComparison.OrdinalIgnoreCase);
}
