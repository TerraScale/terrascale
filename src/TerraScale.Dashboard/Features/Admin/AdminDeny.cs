using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Admin
{
    public static class AdminDeny
    {
        public static async Task<IResult> DenyAsync(HttpContext context, DashboardSessions sessions, IPlatformRoleGateway roles)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            var session = await sessions.ReadForPrivilegeAsync(context);
            if (session is null)
            {
                var target = "/login";
                if (HttpMethods.IsGet(context.Request.Method))
                {
                    target += "?returnTo=" + Uri.EscapeDataString((context.Request.PathBase + context.Request.Path + context.Request.QueryString).ToString()).Replace("%20", "+", StringComparison.Ordinal);
                }

                return HttpRedirects.SeeOther(target);
            }

            if (await roles.PlatformRoleAsync(session, context.RequestAborted) != "admin")
            {
                return RequestError.Response(context, StatusCodes.Status403Forbidden, "Your account does not have platform admin access. Ask an owner or admin for access.");
            }

            return RequestError.Response(context, StatusCodes.Status503ServiceUnavailable, "Platform administration is unavailable. Please try again later.");
        }
    }
}
