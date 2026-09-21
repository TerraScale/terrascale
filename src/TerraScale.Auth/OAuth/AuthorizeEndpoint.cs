namespace TerraScale.Auth.OAuth;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.OAuth.OAuthSupport;

internal sealed class AuthorizeEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/{slug}/oauth/authorize", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, AuthOptions options,
            Users.SessionService sessions, CancellationToken ct)
    {
        if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
            return ApiResults.NotFound();

        var query = request.Query;
        var clientId = query["client_id"].ToString();
        var redirectUri = query["redirect_uri"].ToString();
        var state = query["state"].ToString();

        // RFC 6749 4.1.2.1: only redirect once client_id and redirect_uri are validated.
        if (clientId.Length == 0)
            return ApiResults.BadRequest("invalid_request", "client_id is required");
        if (await store.FindOAuthClientAsync(instance.Id, clientId, ct) is not { } client)
            return ApiResults.BadRequest("invalid_request", "unknown client_id");
        if (redirectUri.Length == 0 || !client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
            return ApiResults.BadRequest("invalid_request", "redirect_uri does not exactly match a registered redirect uri");

        if (query["response_type"].ToString() != "code")
            return RedirectError(redirectUri, state, "unsupported_response_type", "only response_type=code is supported");

        var scopeParameter = query["scope"].ToString();
        var scopes = scopeParameter.Length == 0
            ? ["openid"]
            : scopeParameter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (scopes.Any(scope => !SupportedScopes.Contains(scope)))
            return RedirectError(redirectUri, state, "invalid_scope", "supported scopes: openid profile email");

        var challenge = query["code_challenge"].ToString();
        var challengeMethod = query["code_challenge_method"].ToString();
        if (challenge.Length == 0 && challengeMethod.Length > 0)
            return RedirectError(redirectUri, state, "invalid_request", "code_challenge is required when code_challenge_method is present");
        if (challenge.Length > 0 && challengeMethod != "S256")
            return RedirectError(redirectUri, state, "invalid_request", "only code_challenge_method=S256 is supported");
        if (client.Public && challenge.Length == 0)
            return RedirectError(redirectUri, state, "invalid_request", "PKCE with S256 is required for public clients");

        var cookie = request.Cookies[SessionCookies.Name];
        var resolved = cookie is null ? null : await sessions.ResolveAsync(instance.Id, cookie, ct);
        if (resolved is null)
        {
            if (query["prompt"].ToString() == "none")
                return RedirectError(redirectUri, state, "login_required", "no active session");
            var authorizeUrl = Issuer(options, instance.Slug) + "/oauth/authorize" + request.QueryString.Value;
            return Results.Redirect($"/ui/{instance.Slug}/sign-in?next={Uri.EscapeDataString(authorizeUrl)}");
        }

        // DECISION: no consent page in v1 — first-party clients get the code
        // immediately once the user has a session. Third-party consent UI is a
        // later feature; the authorize POST path it would need is not mapped yet.
        var (session, user) = resolved.Value;
        var nonce = query["nonce"].ToString();
        var code = SecureTokens.RandomToken(48);
        await store.CreateAuthorizationCodeAsync(new AuthorizationCode
        {
            // The session id rides in the record id so the token exchange can
            // issue sid / auth_time claims (the shared model has no session field).
            Id = "oac:" + session.Id + ":" + SecureTokens.RandomToken(12),
            InstanceId = instance.Id,
            ClientId = client.ClientId,
            UserId = user.Id,
            CodeKey = SecureTokens.Sha256Hex(code),
            RedirectUri = redirectUri,
            Scopes = scopes,
            CodeChallenge = challenge.Length > 0 ? challenge : null,
            CodeChallengeMethod = challenge.Length > 0 ? "S256" : null,
            Nonce = nonce.Length > 0 ? nonce : null,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(AuthorizationCodeLifetimeSeconds).ToUnixTimeSeconds(),
            Consumed = false,
        }, ct);
        await AuditAsync(store, instance.Id, "oauth.code_issued", user.Id, new Dictionary<string, object>
        {
            ["client_id"] = client.ClientId,
            ["scope"] = string.Join(' ', scopes),
        }, ct);

        var callback = AppendQuery(redirectUri, "code=" + code);
        return Results.Redirect(state.Length > 0 ? AppendQuery(callback, "state=" + Uri.EscapeDataString(state)) : callback);
    }

    private const int AuthorizationCodeLifetimeSeconds = 60;

    private static IResult RedirectError(string redirectUri, string state, string error, string description)
    {
        var query = $"error={Uri.EscapeDataString(error)}&error_description={Uri.EscapeDataString(description)}";
        if (state.Length > 0)
            query += "&state=" + Uri.EscapeDataString(state);
        return Results.Redirect(AppendQuery(redirectUri, query));
    }
}
