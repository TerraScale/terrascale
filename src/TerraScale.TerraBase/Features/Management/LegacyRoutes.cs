namespace TerraBase.Features.Management;

/// <summary>Keeps old management URLs as hidden aliases until production callers have migrated.</summary>
public static class LegacyRoutes
{
    /// <summary>Rewrites hidden aliases before routing and retains their membership-based identity and database ids.</summary>
    public static WebApplication UseTerraBaseLegacyRoutes(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            var original = context.Request.Path;
            if (original.Equals(new PathString("/v1/node/pressure"), StringComparison.OrdinalIgnoreCase)
                || original.Equals(new PathString("/v1/node/pressure/"), StringComparison.OrdinalIgnoreCase))
            {
                context.Request.Path = "/admin/node/pressure";
            }
            else if ((original.StartsWithSegments(ManagementRoute.Organizations, StringComparison.OrdinalIgnoreCase)
                    || original.StartsWithSegments("/management/organizations", StringComparison.OrdinalIgnoreCase))
                && Alias(context) is { } alias)
            {
                context.Features.Set(alias.Identity);
                context.Request.Path = alias.Path;
            }

            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                context.Request.Path = original;
            }
        });
        return app;
    }

    private static (PathString Path, LegacyManagementRoute Identity)? Alias(HttpContext context)
    {
        var parts = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is { Length: 8 } && parts[0].Equals("management", StringComparison.OrdinalIgnoreCase)
            && parts[^1].Equals("query-shapes", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(context.Request.Method))
        {
            parts = ["v1", .. parts];
        }

        if (parts is null || parts.Length < 7
            || !parts[0].Equals("v1", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("management", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("organizations", StringComparison.OrdinalIgnoreCase)
            || !parts[4].Equals("projects", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var method = context.Request.Method;
        var organization = parts[3];
        var project = parts[5];
        var suffix = parts[6..];
        suffix[0] = suffix[0].ToLowerInvariant();
        if (suffix is ["databases", _, ..] && suffix.Length > 2)
        {
            suffix[2] = suffix[2].ToLowerInvariant();
            if (suffix is ["databases", _, "sql" or "point-in-time", _])
            {
                suffix[3] = suffix[3].ToLowerInvariant();
            }

            if (suffix.Length > 4)
            {
                suffix[4] = suffix[4].ToLowerInvariant();
            }
        }

        var mapped = suffix switch
        {
            ["usage"] when HttpMethods.IsGet(method) => suffix,
            ["saved-queries", ..] when suffix.Length <= 2 => suffix,
            ["databases"] when HttpMethods.IsGet(method) || HttpMethods.IsPost(method) => suffix,
            ["databases", var id, ..] when DatabaseSuffix(suffix[2..], method) => DatabasePath(suffix, id),
            _ => null,
        };
        return mapped is null ? null : ($"/v1/projects/{project}/{string.Join('/', mapped)}", new LegacyManagementRoute(organization, context.Request.Path.Value!));
    }

    private static bool DatabaseSuffix(string[] suffix, string method) => suffix switch
    {
        [] => HttpMethods.IsGet(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method),
        ["branches"] => HttpMethods.IsPost(method),
        ["backups"] => HttpMethods.IsGet(method) || HttpMethods.IsPost(method),
        ["backups", _, "restore"] => HttpMethods.IsPost(method),
        ["backup-policy"] => HttpMethods.IsGet(method) || HttpMethods.IsPut(method),
        ["point-in-time", "plan"] => HttpMethods.IsPost(method),
        ["indexes"] => HttpMethods.IsGet(method) || HttpMethods.IsPost(method),
        ["indexes", _] => HttpMethods.IsDelete(method),
        ["index-recommendations"] => HttpMethods.IsGet(method),
        ["index-recommendations", _, "apply"] => HttpMethods.IsPost(method),
        ["collections"] or ["collections", _, "schema"] or ["usage"] or ["query-shapes"] => HttpMethods.IsGet(method),
        ["sql"] or ["sql", "check"] => HttpMethods.IsPost(method),
        _ => false,
    };

    private static string[] DatabasePath(string[] suffix, string id) => suffix switch
    {
        ["databases", _, "collections"] => ["databases", id, "schema"],
        ["databases", _, "collections", var collection, "schema"] => ["databases", id, "schema", collection],
        _ => suffix,
    };
}
