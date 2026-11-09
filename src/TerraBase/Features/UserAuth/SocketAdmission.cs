using System.Security.Claims;
using TerraBase.Errors;

using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

internal sealed record SocketRefusal(string Code, string Message);

internal static class SocketAdmission
{
    internal static ValueTask<SocketRefusal?> AdmitSocketAsync(HttpContext context, CancellationTokenSource revoked, Action<IDisposable> keep) =>
        AdmitCoreAsync(context, revoked, keep, shape: false);

    internal static ValueTask<SocketRefusal?> AdmitShapeSocketAsync(HttpContext context, CancellationTokenSource revoked, Action<IDisposable> keep) =>
        AdmitCoreAsync(context, revoked, keep, shape: true);

    private static async ValueTask<SocketRefusal?> AdmitCoreAsync(HttpContext context, CancellationTokenSource revoked, Action<IDisposable> keep, bool shape)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(revoked);
        ArgumentNullException.ThrowIfNull(keep);
        if (UserAuthExtensions.Identity(context.User) is not { } jwt)
        {
            return null;
        }

        if (!shape && jwt.HasClaim("token_kind", "end_user"))
        {
            return new SocketRefusal("forbidden", "end-user tokens cannot run SQL");
        }

        if (jwt.FindFirst("project_id")?.Value is not { } project)
        {
            return new SocketRefusal("invalid_key", "this token does not authorize project routes; use an API key or its project token");
        }

        if (context.Request.RouteValues["project"] as string != project)
        {
            return new SocketRefusal("unknown_database", "the database does not exist");
        }

        var token = context.Features.Get<ProjectToken>();
        if (token is { Allowlists.Count: > 0 } && IpAllowlists.ClientOf(context) is var client && !IpAllowlists.Admits(token.Allowlists, client))
        {
            return new SocketRefusal("ip_not_allowed", IpAllowlists.Refusal(client));
        }

        switch (await ProjectGrants.GrantAsync(context, jwt, project, bucketCreation: false).ConfigureAwait(false))
        {
            case ProjectAccess granted:
                context.Features.Set(granted);
                break;
            case NotFound:
                return new SocketRefusal("unknown_database", "the database does not exist");
            case Unavailable unavailable:
                return new SocketRefusal("unavailable", unavailable.Message);
        }

        if (token?.Key is not { } key)
        {
            return null;
        }

        var authority = context.RequestServices.GetRequiredService<IKeyAuthority>();
        switch ((await authority.AdmitAsync(key, context.RequestAborted).ConfigureAwait(false)).Value)
        {
            case KeyActive active:
                if (KeyWatch.Start(authority, watched: true, active.Until, _ => ValueTask.FromResult<KeyToken?>(key), revoked,
                    context.RequestServices.GetRequiredService<TimeProvider>()) is { } watch)
                {
                    keep(watch);
                }

                return null;
            case KeyRefused refused:
                return new SocketRefusal(refused.Code, KeyAuthorityMessages.Refused(refused.Code));
            default:
                return new SocketRefusal("auth_unavailable", KeyAuthorityMessages.Unavailable);
        }
    }

    internal static Task RefuseKeyAsync(HttpContext context, KeyRefused? refused)
    {
        if (refused is not null)
        {
            context.Response.Headers.WWWAuthenticate = $"Bearer error=\"invalid_token\", error_description=\"{refused.Code}\"";
            return BadRequests.WriteAsync(context, StatusCodes.Status401Unauthorized, refused.Code, KeyAuthorityMessages.Refused(refused.Code), op: null);
        }

        context.Response.Headers.RetryAfter = "1";
        return BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "auth_unavailable", KeyAuthorityMessages.Unavailable, op: null);
    }
}
