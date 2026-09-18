namespace TerraScale.Dashboard2;

/// <summary>Session cookie guard. Public paths pass through; everything else
/// requires a signed-in dashboard session (cookie set by the login endpoint).</summary>
public sealed class DashboardAuthMiddleware(RequestDelegate next)
{
    public const string SessionCookie = "ts_session";
    public const string SessionToken = "demo-session-token";

    private static readonly HashSet<string> PublicPrefixes =
    [
        "/login", "/reset-password", "/invitations/", "/cli/", "/agent/", "/oauth/",
        "/api/", "/health/", "/css/", "/js/", "/fonts/", "/brand/", "/favicon", "/openapi", "/scalar",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (PublicPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || path == "/")
        {
            // "/" is guarded by the page itself (redirects to /login when anonymous).
            await next(context);
            return;
        }

        if (context.Request.Cookies[SessionCookie] != SessionToken)
        {
            context.Response.Redirect("/login");
            return;
        }

        await next(context);
    }

    public static bool IsAuthenticated(HttpContext context) =>
        context.Request.Cookies[SessionCookie] == SessionToken;
}

public interface IPageModel
{
    ShellModel Shell { get; init; }
}

public static class ShellFactory
{
    public static ShellModel Create(DashboardStore store, string href)
    {
        var user = store.CurrentUser;
        var route = NavRoute.Find(href);
        return new ShellModel(
            ActiveHref: href,
            Route: route,
            Store: store,
            AccountName: user.Name.Length > 0 ? user.Name : user.Email,
            AccountEmail: user.Email,
            AccountInitials: store.Initials(user.Name, user.Email),
            UnreadNotifications: store.UnreadNotifications,
            PageTitle: route?.Label ?? "TerraScale");
    }
}
