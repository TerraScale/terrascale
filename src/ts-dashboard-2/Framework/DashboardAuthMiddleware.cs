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
        "/onboarding", "/checkout/",
        "/api/v1/auth/", "/health/", "/css/", "/js/", "/fonts/", "/brand/", "/favicon", "/openapi", "/scalar",
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
