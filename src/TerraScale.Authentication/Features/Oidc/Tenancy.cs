using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Store;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>
/// The tenant of a hosted page (<c>/ui/{tenant}/…</c>) or protocol request (<c>/{tenant}/…</c>): <c>terrascale</c>, or
/// a project that exists (project-tenant-management.md). Resolved once per request before authentication, so the
/// OpenIddict stores, the hosted pages and the session cookie all read the same tenant; a request that names no tenant
/// (the management APIs, background work) is the platform's.
/// </summary>
public static class Tenancy
{
    internal const string ItemKey = "ts-auth:tenant";

    // The protocol routes a project tenant serves; device flow, API keys and token exchange stay the platform's.
    private static readonly string[] ProjectRoutes =
        [OidcIssuer.ConfigurationPath, OidcIssuer.JwksPath, "oauth/authorize", "oauth/token", "oauth/userinfo", "oauth/revoke", "oauth/logout"];

    /// <summary>Resolves the request's tenant into the request items.</summary>
    public static void UseTenancy(this WebApplication app) => app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        if (await ResolveAsync(context.RequestServices.GetRequiredService<AuthStore>(), context.Request.Path, context.RequestAborted) is { } tenant)
        {
            context.Items[ItemKey] = tenant;
        }

        await next(context);
    });

    /// <summary>The tenant the request's path names, or <see langword="null"/> when it names none or an unknown one.</summary>
    public static string? Resolved(HttpContext context) => context.Items[ItemKey] as string;

    /// <summary>The instance the request acts in: its resolved tenant, else the platform's.</summary>
    public static string Current(HttpContext? context) => context?.Items[ItemKey] as string ?? OidcIssuer.InstanceId;

    /// <summary>Whether <paramref name="tenant"/> is a project's.</summary>
    public static bool IsProject(string tenant) => tenant != OidcIssuer.InstanceId;

    /// <summary>The hosted session cookie of a tenant, so a project sign-in never replaces the platform's.</summary>
    public static string SessionCookie(string tenant) =>
        IsProject(tenant) ? SecuritySchemes.SessionCookieName + "_" + tenant : SecuritySchemes.SessionCookieName;

    private static async Task<string?> ResolveAsync(AuthStore store, PathString path, CancellationToken cancellationToken)
    {
        var segments = (path.Value ?? string.Empty).Split('/', 4);
        var (tenant, rest) = segments switch
        {
            [_, "signin", _] => (OidcIssuer.Slug, (string?)null),
            [_, "ui", var named, ..] => (named, (string?)null),
            [_, var named, var route] => (named, route),
            [_, var named, var first, var second] => (named, first + "/" + second),
            _ => (string.Empty, null),
        };
        if (string.Equals(tenant, OidcIssuer.Slug, StringComparison.OrdinalIgnoreCase))
        {
            // Endpoint matching ignores case, as routing does; the issuer stays the canonical slug.
            return OidcIssuer.Slug;
        }

        var page = segments is [_, "ui", ..];
        if (!TerraScaleIds.IsValid(tenant) || !page && !ProjectRoutes.Contains(rest?.TrimEnd('/'), StringComparer.Ordinal))
        {
            return null;
        }

        try
        {
            // Discovery and the JWKS decide nothing about authorization: this region's copy may answer. Sign-in and
            // tokens read the project itself, so a deleted project stops serving them at once.
            var metadata = rest is OidcIssuer.ConfigurationPath or OidcIssuer.JwksPath;
            var project = metadata
                ? await store.GetLocalAsync(AuthStore.ProjectCollection, OidcIssuer.InstanceId, tenant, cancellationToken).ConfigureAwait(false)
                : await store.GetAsync(AuthStore.ProjectCollection, OidcIssuer.InstanceId, tenant, cancellationToken).ConfigureAwait(false);
            return project is null ? null : tenant;
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            throw ApiException.Unavailable(error);
        }
    }
}
