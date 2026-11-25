using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Oidc;

public static class Tenancy
{
    internal const string ItemKey = "ts-auth:tenant";

    private static readonly string[] ProjectRoutes =
        ["step-up", OidcIssuer.ConfigurationPath, OidcIssuer.JwksPath, "oauth/authorize", "oauth/token", GuestSignIn.Route, "oauth/userinfo", "oauth/revoke", "oauth/logout"];

    public static void UseTenancy(this WebApplication app) => app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        var resolution = await ResolveAsync(context, context.RequestAborted);
        if (resolution.Throttled)
        {
            await AnonymousTenantReads.RefuseAsync(context);
            return;
        }

        if (resolution.Tenant is { } tenant)
        {
            context.Items[ItemKey] = tenant;
        }

        await next(context);
    });

    public static string? Resolved(HttpContext context) => context.Items[ItemKey] as string;

    public static string Current(HttpContext? context) => context?.Items[ItemKey] as string ?? OidcIssuer.InstanceId;

    public static bool IsProject(string tenant) => tenant is not (OidcIssuer.InstanceId or AgentIdIssuer.Slug);

    public static string SessionCookie(string tenant) =>
        IsProject(tenant) ? SecuritySchemes.SessionCookieName + "_" + tenant : SecuritySchemes.SessionCookieName;

    private readonly record struct Resolution(string? Tenant, bool Throttled = false);

    private static async Task<Resolution> ResolveAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var segments = (context.Request.Path.Value ?? string.Empty).Split('/', 4);
        var (tenant, rest) = segments switch
        {
            [_, "v1", var project, "step-up"] => (project, "step-up"),
            [_, "signin", _] => (OidcIssuer.Slug, (string?)null),
            [_, "ui", var named, ..] => (named, (string?)null),
            [_, var named, var route] => (named, route),
            [_, var named, var first, var second] => (named, first + "/" + second),
            _ => (string.Empty, null),
        };
        if (string.Equals(tenant, OidcIssuer.Slug, StringComparison.OrdinalIgnoreCase))
        {
            return new(OidcIssuer.Slug);
        }

        if (string.Equals(tenant, AgentIdIssuer.Slug, StringComparison.OrdinalIgnoreCase))
        {
            return new(AgentIdIssuer.Routes.Contains(rest?.TrimEnd('/'), StringComparer.OrdinalIgnoreCase) ? AgentIdIssuer.Slug : null);
        }

        var page = segments is [_, "ui", ..];
        if (!TerraScaleIds.IsValid(tenant) || !page && !ProjectRoutes.Contains(rest?.TrimEnd('/'), StringComparer.Ordinal))
        {
            return new(null);
        }

        if (!AnonymousTenantReads.Admit(context))
        {
            return new(null, Throttled: true);
        }

        try
        {
            var (data, platform) = (context.RequestServices.GetRequiredService<AuthData>(), OidcIssuer.InstanceId);
            return new(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectRecord>(
                "SELECT * FROM projects WHERE id = @tenant AND instance_id = @platform", new { tenant, platform }), cancellationToken) is { } project
                && PlatformTenant.IsCustomerProject(project) ? tenant : null);
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            throw ApiException.Unavailable(error);
        }
    }
}
