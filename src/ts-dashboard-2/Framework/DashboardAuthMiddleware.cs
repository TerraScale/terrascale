namespace TerraScale.Dashboard2;

/// <summary>Session cookie guard. Public paths pass through; everything else
/// requires a signed-in dashboard session and points the store at that
/// session's account world for the rest of the request.</summary>
public sealed class DashboardAuthMiddleware(RequestDelegate next, DashboardStore store, DashboardSecurityOptions security, LiveIdentityProvider? identityProvider = null, DashboardStateRepository? stateRepository = null)
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
        var stateConflict = false;
        store.ResetRequestActivation();

        try
        {
            var isPublic = PublicPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (!security.DemoMode)
            {
                // Demo login and simulated OAuth/passkey flows are never reachable in live mode.
                isPublic = path.Equals("/login", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/health/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/fonts/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/brand/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/scalar", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/api/v1/auth/me", StringComparison.OrdinalIgnoreCase);

                var bearer = context.Request.Headers.Authorization.ToString();
                if (bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && identityProvider is not null)
                {
                    var identity = await identityProvider.IntrospectAsync(bearer["Bearer ".Length..].Trim(), context.RequestAborted);
                    if (identity is not null && stateRepository is not null)
                    {
                        await store.TryActivateExternalIdentityAsync(identity.Subject, identity.Email, identity.DisplayName, stateRepository, context.RequestAborted);
                        isPublic = true;
                    }
                }
            }
            else
            {
                store.TryActivate(token);
            }

            if (isPublic)
            {
                await next(context);
                return;
            }

            if (!security.DemoMode && IsUnsafe(context.Request.Method)
                && context.Request.Cookies.ContainsKey(SessionCookie)
                && !HasSameOrigin(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "csrf_origin_rejected" }, context.RequestAborted);
                return;
            }

            if (!store.IsAuthenticated(context))
            {
                if (security.DemoMode)
                    context.Response.Redirect("/login");
                else
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        }
        catch (DashboardStateConflictException) when (!context.Response.HasStarted)
        {
            stateConflict = true;
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "state_conflict",
                message = "This account changed in another dashboard session. Reload and retry.",
            }, context.RequestAborted);
        }
        finally
        {
            if (!stateConflict && !security.DemoMode && store.IsAuthenticated(context) && stateRepository is not null)
            {
                try
                {
                    await store.PersistExternalIdentityAsync(stateRepository, context.RequestAborted);
                }
                catch (DashboardStateConflictException) when (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "state_conflict",
                        message = "This account changed in another dashboard session. Reload and retry.",
                    }, context.RequestAborted);
                }
            }
        }
    }

    private static bool IsUnsafe(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    private static bool HasSameOrigin(HttpRequest request)
    {
        var candidate = request.Headers.Origin.ToString();
        if (candidate.Length == 0)
            candidate = request.Headers.Referer.ToString();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var origin)) return false;
        var requestPort = request.Host.Port ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        var originPort = origin.Port == -1
            ? (string.Equals(origin.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
            : origin.Port;
        return string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && originPort == requestPort;
    }
}
