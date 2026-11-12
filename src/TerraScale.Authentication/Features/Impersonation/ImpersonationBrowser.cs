using System.Collections;
using System.Diagnostics.CodeAnalysis;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;

namespace TerraScale.Authentication.Features.Impersonation;

public static class ImpersonationBrowser
{
    public static CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        Path = "/",
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Expires = expires,
    };

    public static void UseImpersonation(this WebApplication app) => app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        var tenant = Tenancy.Current(context);
        if (context.Request.Cookies.TryGetValue(Impersonations.Cookie(tenant), out var cookie))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Items[Impersonations.Item] = await context.RequestServices.GetRequiredService<Impersonations>()
                .ResolveAsync(tenant, cookie, context.RequestAborted);
            context.Request.Cookies = new ImpersonationCookies(context.Request.Cookies, Tenancy.SessionCookie(tenant));
            if (context.Request.Path.StartsWithSegments($"/ui/{tenant}/social")
                || context.Request.Path.StartsWithSegments("/ui") && !HttpMethods.IsGet(context.Request.Method)
                    && !HttpMethods.IsHead(context.Request.Method)
                    && !context.Request.Path.Equals(new PathString($"/ui/{tenant}/impersonation/end")))
            {
                throw new ApiException(403, "impersonation_read_only",
                    "You cannot change a password, email, username, MFA factor, passkey, linked provider or delete an account while impersonating. End the impersonation first.");
            }
        }

        await next(context);
    });

    private sealed class ImpersonationCookies(IRequestCookieCollection cookies, string hidden) : IRequestCookieCollection
    {
        private readonly Dictionary<string, string> visible = cookies.Where(pair => pair.Key != hidden).ToDictionary();

        public int Count => this.visible.Count;

        public ICollection<string> Keys => this.visible.Keys;

        public string? this[string key] => this.visible.GetValueOrDefault(key);

        public bool ContainsKey(string key) => this.visible.ContainsKey(key);

        public bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => this.visible.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => this.visible.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
    }
}
