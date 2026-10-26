using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Admin;

// Only a freshly rotated, signed platform access token can authorize an
// operator. Organization roles and local account fields confer no such role.
public static class AdminEndpoints
{
    public static void MapAdmin(this WebApplication app)
    {
        app.MapMethods("/admin", ["GET", "HEAD"], DenyAsync);
        app.MapMethods("/admin/{**section}", ["GET", "HEAD"], DenyAsync);
        app.MapPost("/api/v1/admin/users/{accountID}", DenyAsync);
        app.MapPost("/api/v1/admin/organizations/{accountID}/{organizationID}", DenyAsync);
        app.MapPost("/api/v1/admin/projects/{accountID}/{organizationID}/{projectID}", DenyAsync);
        app.MapPost("/api/v1/admin/estate/action", DenyAsync);
        app.MapPost("/api/v1/admin/plans", DenyAsync);
        app.MapPost("/api/v1/admin/plans/{planID}/visibility", DenyAsync);
        app.MapPost("/api/v1/admin/plans/{planID}/delete", DenyAsync);
    }

    private static async Task<IResult> DenyAsync(HttpContext context, DashboardSessions sessions, IPlatformRoleGateway roles)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        var session = await sessions.ReadForPrivilegeAsync(context);
        if (session is null)
        {
            var target = "/login";
            if (HttpMethods.IsGet(context.Request.Method))
                target += "?returnTo=" + Uri.EscapeDataString((context.Request.PathBase + context.Request.Path + context.Request.QueryString).ToString()).Replace("%20", "+", StringComparison.Ordinal);
            return HttpRedirects.SeeOther(target);
        }

        if (await roles.PlatformRoleAsync(session, context.RequestAborted) != "admin")
            return RequestError.Response(context, StatusCodes.Status403Forbidden, "Your account does not have platform admin access. Ask an owner or admin for access.");

        // These original admin operation contracts have not been published by
        // their owning providers. Verified authority cannot invent inventories
        // or permit local substitutes for provider mutations.
        return RequestError.Response(context, StatusCodes.Status503ServiceUnavailable, "Platform administration is unavailable. Please try again later.");
    }
}
