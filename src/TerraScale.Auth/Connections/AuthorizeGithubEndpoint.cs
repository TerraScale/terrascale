namespace TerraScale.Auth.Connections;

using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Endpoints;

/// <summary>Starts a GitHub OAuth app sign-in with a single-use state token.</summary>
internal sealed class AuthorizeGithubEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/connections/{connectionId}/github/authorize", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string connectionId, HttpContext context, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ConnectionFlow.NotFoundPage();
        var connection = await ConnectionFlow.FindConnectionAsync(store, instance.Id, connectionId, "github", ct);
        if (connection is null)
            return ConnectionFlow.NotFoundPage();
        if (connection.AuthorizationEndpoint is null)
            return ConnectionFlow.ErrorPage(slug, "This GitHub connection is incomplete.");
        var state = await ConnectionFlow.MintStateAsync(store, instance.Id, connection.Id, "github.state", null, ConnectionFlow.OidcStateTtl, ct);
        context.Response.Cookies.Append(StateCookieName, SecureTokens.Sha256Hex(state), StateCookieOptions(slug, connection.Id));
        var callback = GithubCallbackUrl(options, slug, connection.Id);
        var separator = connection.AuthorizationEndpoint.Contains('?') ? '&' : '?';
        return Results.Redirect($"{connection.AuthorizationEndpoint}{separator}client_id={Uri.EscapeDataString(connection.ClientId!)}&redirect_uri={Uri.EscapeDataString(callback)}&scope=user%3Aemail&state={Uri.EscapeDataString(state)}");
    }

    internal static string GithubCallbackUrl(AuthOptions options, string slug, string connectionId) =>
        $"{options.PublicOrigin.ToString().TrimEnd('/')}/ui/{slug}/connections/{connectionId}/github/callback";

    internal const string StateCookieName = "tsauth_github_state";

    internal static CookieOptions StateCookieOptions(string slug, string connectionId) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = $"/ui/{slug}/connections/{connectionId}/github/callback",
        MaxAge = ConnectionFlow.OidcStateTtl,
        IsEssential = true,
    };
}
