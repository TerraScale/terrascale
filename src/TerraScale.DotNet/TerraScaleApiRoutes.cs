namespace TerraScale.DotNet;

/// <summary>A fully-bound public API route: the request path with query string.</summary>
internal readonly record struct TerraScaleBoundRoute(string RequestUri)
{
    public override string ToString() => RequestUri;
}

internal static class TerraScaleApiRoutes
{
    public static TerraScaleBoundRoute DatabaseBranchesCreate(string projectId) =>
        new($"/api/v1/projects/{Escape(projectId)}/database-branches");

    public static TerraScaleBoundRoute DatabaseBranchesCapabilityGet(string projectId) =>
        new($"/api/v1/projects/{Escape(projectId)}/database-branches/capability");

    private static string Escape(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Uri.EscapeDataString(value.Trim());
    }
}
