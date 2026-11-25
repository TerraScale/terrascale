namespace TerraScale.Dashboard.Platform.Identity;

public static class DashboardCookies
{
    public static CookieOptions Private(HttpContext context, SameSiteMode sameSite, DateTimeOffset? expires = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new() { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = sameSite, Path = "/", Expires = expires, IsEssential = true };
    }
}
