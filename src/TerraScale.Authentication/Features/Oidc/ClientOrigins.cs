using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Oidc;

public static class ClientOrigins
{
    public const int MaxPerClient = 10;

    private static readonly string[] MutationRoutes = ["oauth/token", GuestSignIn.Route, "oauth/revoke"];

    private const string UserInfoRoute = "oauth/userinfo";

    public static string? Canonical(string? value) =>
        value is { Length: > 0 and <= 255 } && value.All(char.IsAscii) && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        && uri.UserInfo.Length == 0 && uri.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
        && uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) == value
            ? value : null;

    public static Valid<IReadOnlyList<string>> Validate(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return new Valid<IReadOnlyList<string>>([]);
        }

        List<string> origins = [];
        foreach (var value in values)
        {
            if (Canonical(value) is not { } origin)
            {
                return Invalid();
            }

            origins.Add(origin);
        }

        return origins.Count <= MaxPerClient && origins.Distinct(StringComparer.Ordinal).Count() == origins.Count ? origins : Invalid();
    }

    private static Validation Invalid() => new("allowed_origins holds at most 10 unique exact origins: https://host[:port], or http on a loopback host, in lowercase, without a default port, path, trailing slash, wildcard, userinfo, query or fragment.");

    public static void UseClientCors(this WebApplication app) => app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        if (Tenancy.Resolved(context) is not { } tenant || !Tenancy.IsProject(tenant) || Methods(context.Request.Path) is not { } methods
            || context.Request.Headers.Origin is not [{ Length: > 0 } origin])
        {
            await next(context);
            return;
        }

        var allowed = await AllowsAsync(context.RequestServices.GetRequiredService<AuthData>(), tenant, origin, context.RequestAborted);
        if (HttpMethods.IsOptions(context.Request.Method) && context.Request.Headers.ContainsKey(HeaderNames.AccessControlRequestMethod))
        {
            context.Response.Headers.Vary = HeaderNames.Origin;
            if (allowed)
            {
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.AccessControlAllowMethods = methods;
                context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type";
                context.Response.Headers.AccessControlMaxAge = "600";
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Origin);
            if (allowed)
            {
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.AccessControlExposeHeaders = "Retry-After, WWW-Authenticate";
            }

            return Task.CompletedTask;
        });
        await next(context);
    });

    private static string? Methods(PathString path) =>
        (path.Value ?? string.Empty).TrimEnd('/').Split('/') is not [_, _, var first, var second] ? null
        : string.Equals(first + "/" + second, UserInfoRoute, StringComparison.OrdinalIgnoreCase) ? HttpMethods.Get
        : MutationRoutes.Contains(first + "/" + second, StringComparer.OrdinalIgnoreCase) ? HttpMethods.Post
        : null;

    private static async Task<bool> AllowsAsync(AuthData data, string tenant, string origin, CancellationToken cancellationToken)
    {
        var registered = await data.ReadAsync((db, token) => db.OidcApplications.Where(client => client.InstanceId == tenant)
            .Select(client => client.AllowedOrigins).ToListAsync(token), cancellationToken);
        return registered.Any(origins => origins.Contains(origin, StringComparer.Ordinal));
    }
}
