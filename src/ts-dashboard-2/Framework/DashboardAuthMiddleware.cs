namespace TerraScale.Dashboard2;

/// <summary>Session cookie guard. Public paths pass through; everything else
/// requires a signed-in dashboard session and points the store at that
/// session's account world for the rest of the request.</summary>
public sealed class DashboardAuthMiddleware(RequestDelegate next, DashboardStore store)
{
    public const string SessionCookie = "ts_session";

    private static readonly HashSet<string> PublicPrefixes =
    [
        "/login", "/reset-password", "/invitations/", "/cli/", "/agent/", "/oauth/",
        "/onboarding", "/checkout/",
        "/api/v1/auth/", "/health/", "/css/", "/js/", "/fonts/", "/brand/", "/favicon", "/openapi", "/scalar",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        var token = context.Request.Cookies[SessionCookie];
        store.TryActivate(token);
        if (PublicPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        if (token is null || !store.IsAuthenticated(context))
        {
            context.Response.Redirect("/login");
            return;
        }

        await next(context);
    }
}
