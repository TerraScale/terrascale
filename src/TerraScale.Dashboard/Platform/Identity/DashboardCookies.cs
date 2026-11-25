namespace TerraScale.Dashboard.Platform.Identity;

public static class DashboardCookies
{
    private const string HostPrefix = "__Host-";

    public static CookieOptions Private(HttpContext context, SameSiteMode sameSite, DateTimeOffset? expires = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new() { HttpOnly = true, Secure = true, SameSite = sameSite, Path = "/", Expires = expires, IsEssential = true };
    }

    public static string? Read(HttpContext context, string name)
    {
        return context.Request.Cookies[name] is { } current ? current
            : name.StartsWith(HostPrefix, StringComparison.Ordinal) ? context.Request.Cookies[name[HostPrefix.Length..]] : null;
    }

    public static void Delete(HttpContext context, string name, SameSiteMode sameSite)
    {
        context.Response.Cookies.Delete(name, Private(context, sameSite));
        if (name.StartsWith(HostPrefix, StringComparison.Ordinal) && context.Request.Cookies.ContainsKey(name[HostPrefix.Length..]))
        {
            context.Response.Cookies.Delete(name[HostPrefix.Length..], new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = sameSite, Path = "/" });
        }
    }
}
