using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.Shared.Accounts;

public static class WorkspaceUrl
{
    public static string For(AccountContext context, string path, bool replace = false) =>
        With(path, context.Organization?.Id, context.Project?.Id, replace);

    public static string PreserveRequest(HttpContext http, string path)
    {
        // Invitations carry their own verified scope; caller workspace queries do not choose their landing.
        if (http.Request.Path.StartsWithSegments("/invitations")) return path;
        var queryAt = path.IndexOf('?');
        var query = QueryHelpers.ParseQuery(queryAt < 0 ? "" : path[(queryAt + 1)..].Split('#')[0]);
        if (query.ContainsKey("organizationId") || query.ContainsKey("projectId")) return path;
        return With(path, http.Request.Query["organizationId"].ToString(), http.Request.Query["projectId"].ToString(), false);
    }

    private static string With(string path, string? organizationId, string? projectId, bool replace)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("/login", StringComparison.Ordinal)) return path;
        var fragmentAt = path.IndexOf('#');
        var fragment = fragmentAt < 0 ? "" : path[fragmentAt..];
        var target = fragmentAt < 0 ? path : path[..fragmentAt];
        var queryAt = target.IndexOf('?');
        var query = QueryHelpers.ParseQuery(queryAt < 0 ? "" : target[queryAt..]);
        if (replace) { query.Remove("organizationId"); query.Remove("projectId"); }
        if (!string.IsNullOrEmpty(organizationId) && !query.ContainsKey("organizationId")) query["organizationId"] = organizationId;
        if (!string.IsNullOrEmpty(projectId) && !query.ContainsKey("projectId") &&
            (!query.TryGetValue("organizationId", out var targetOrganization) || targetOrganization == organizationId)) query["projectId"] = projectId;
        return (queryAt < 0 ? target : target[..queryAt]) + QueryString.Create(query) + fragment;
    }
}
