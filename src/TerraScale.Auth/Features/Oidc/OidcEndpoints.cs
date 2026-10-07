using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using Microsoft.AspNetCore;
using OpenIddict.Server.AspNetCore;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Headless;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Platform;
using TerraScale.Auth.Features.Ui;
using TerraScale.Auth.Hosting;
using TerraScale.Endpoints;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Auth.Features.Oidc;

/// <summary>An OAuth 2.0 error response (RFC 6749 §5.2).</summary>
/// <param name="Error">Error code, for example <c>invalid_grant</c>.</param>
/// <param name="ErrorDescription">Human-readable detail.</param>
public sealed record OAuthErrorBody(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

/// <summary>A token response (RFC 6749 §5.1, OpenID Connect Core §3.1.3.3).</summary>
/// <param name="AccessToken">ES256 JWT access token.</param>
/// <param name="TokenType">Always <c>Bearer</c>.</param>
/// <param name="ExpiresIn">Access token lifetime in seconds.</param>
/// <param name="Scope">Granted scopes.</param>
/// <param name="IdToken">ES256 identity token, when <c>openid</c> was granted.</param>
/// <param name="RefreshToken">Opaque single-use refresh token, when <c>offline_access</c> was granted.</param>
public sealed record TokenBody(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken);

/// <summary>Claims about the signed-in user (OpenID Connect Core §5.3).</summary>
/// <param name="Subject">The user id.</param>
/// <param name="Email">The user's email, with the <c>email</c> scope.</param>
/// <param name="EmailVerified">Always true with the <c>email</c> scope: sign-in proves the address.</param>
public sealed record UserInfoBody(
    [property: JsonPropertyName("sub")] string Subject,
    [property: JsonPropertyName("email"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email,
    [property: JsonPropertyName("email_verified"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? EmailVerified);

/// <summary><c>GET /{slug}/.well-known/openid-configuration</c>: OpenID Provider metadata, served by OpenIddict.</summary>
public sealed class DiscoveryEndpoint : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/{slug}/.well-known/openid-configuration");
        Name("oidc_discovery");
        Summary("OpenID Provider metadata");
        Description("OpenID Connect Discovery 1.0 metadata for the issuer {public origin}/{slug}. Only the slug terrascale exists; others answer 404.");
        Tags("OAuth");
        Produces<Dictionary<string, object>>();
        Produces<OAuthErrorBody>(StatusCodes.Status404NotFound);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override Task<IResult> HandleAsync(CancellationToken cancellationToken) => Task.FromResult(Results.NotFound());
}

/// <summary><c>GET /{slug}/.well-known/jwks.json</c>: the public keys of the issuer's key ring, served by OpenIddict.</summary>
public sealed class JwksEndpoint : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/{slug}/.well-known/jwks.json");
        Name("oidc_jwks");
        Summary("JSON Web Key Set");
        Description("Every ES256 key in the issuer's key ring, read from storage on each request, so tokens signed before a rotation keep validating.");
        Tags("OAuth");
        Produces<Dictionary<string, object>>();
        Produces<OAuthErrorBody>(StatusCodes.Status404NotFound);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override Task<IResult> HandleAsync(CancellationToken cancellationToken) => Task.FromResult(Results.NotFound());
}

/// <summary>
/// <c>GET /{slug}/oauth/authorize</c>: the authorization endpoint (code flow with S256 PKCE). OpenIddict validates the
/// client, redirect URI, scopes and PKCE first; this signs in the user of the hosted session cookie, or sends the
/// browser to the hosted sign-in page and back.
/// </summary>
public sealed class AuthorizeEndpoint(Sessions sessions, TimeProvider clock, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/{slug}/oauth/authorize");
        Name("oauth_authorize");
        Summary("Authorization endpoint");
        Description("Authorization code flow (response_type=code) with PKCE code_challenge_method=S256 required. Without a live tsauth_session cookie the browser is redirected to /ui/{slug}/sign-in and returns here. prompt=none answers login_required without a session; prompt=login, consent and select_account are not supported and answer login_required, consent_required and account_selection_required. Errors after client and redirect validation are redirected to the client. The response is a 302 to the client, or with response_mode=form_post a 200 page that posts the result to it.");
        Tags("OAuth");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status404NotFound);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async Task<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { } request)
        {
            return Results.NotFound();
        }

        foreach (var (prompt, error) in new[] { (PromptValues.Login, Errors.LoginRequired), (PromptValues.Consent, Errors.ConsentRequired), (PromptValues.SelectAccount, Errors.AccountSelectionRequired) })
        {
            if (request.HasPromptValue(prompt))
            {
                return OidcResults.Forbid(error, $"prompt={prompt} is not supported.");
            }
        }

        var live = await sessions.ResolveAsync(OidcIssuer.InstanceId, this.HttpContext.Request.Cookies[SecuritySchemes.SessionCookieName], cancellationToken);
        var authenticatedAt = live?.Session.ExpiresAt - Sessions.Lifetime;
        if (live is not { } signedIn || request.MaxAge is { } maxAge && authenticatedAt!.Value.AddSeconds(maxAge) < clock.GetUtcNow())
        {
            if (request.HasPromptValue(PromptValues.None) || live is not null)
            {
                return OidcResults.Forbid(Errors.LoginRequired, "The user must sign in.");
            }

            var resume = $"/{OidcIssuer.Slug}/oauth/authorize{this.HttpContext.Request.QueryString}";
            return Results.Redirect($"/ui/{OidcIssuer.Slug}/sign-in?next={Uri.EscapeDataString(resume)}");
        }

        return OidcResults.SignIn(signedIn.User, authenticatedAt!.Value, request.GetScopes(), OidcIssuer.SignInAudiences(options));
    }
}

/// <summary>
/// <c>POST /{slug}/oauth/token</c>: redeems an authorization code, rotates a refresh token, or issues a service client's
/// client-credentials access token. Each code and refresh token works once: a second presentation, even a concurrent
/// one, fails and revokes every token of that sign-in. Tokens are refused once the user signed out everywhere.
/// </summary>
public sealed class TokenEndpoint(Users users, HeadlessAuth headless, PlatformGrants platform, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/{slug}/oauth/token");
        Name("oauth_token");
        Summary("Token endpoint");
        Description("application/x-www-form-urlencoded grant_type=authorization_code (code, redirect_uri, code_verifier), grant_type=refresh_token (refresh_token) or, for service clients only, grant_type=client_credentials. For ts-dashboard only, the headless extension grants (TS-582) sign a user in from credentials the client collected: grant_type=urn:terrascale:params:oauth:grant-type:password (email, password, ip_address) and grant_type=urn:terrascale:params:oauth:grant-type:email-code (email, code, ip_address; a sign-in code, or a sign-up code with the pending_token its sign-up answered, which creates the account). ip_address is the end user's IP address; each IP (IPv6 per /64, and 120 per /48) may make 30 headless calls a minute and each email 10 password attempts per 15 minutes, beyond which the answer is 429 rate_limited with Retry-After. Every wrong email, password or code answers invalid_grant alike. Confidential clients authenticate with client_secret_basic or client_secret_post. Access tokens are ES256 JWTs with typ at+jwt whose aud holds TS_AUTH_ACCESS_TOKEN_AUDIENCES; a user's carry email and email_verified, a service client's have sub = client id and no scopes. Refresh tokens rotate on every use and expire 14 days after the authorization code is redeemed.");
        Tags("OAuth");
        Accepts<Dictionary<string, string>>("application/x-www-form-urlencoded");
        Produces<TokenBody>();
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    /// <inheritdoc />
    public override async Task<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { } request)
        {
            return Results.NotFound();
        }

        if (request.GrantType is HeadlessGrants.Password or HeadlessGrants.EmailCode)
        {
            // OpenIddict has authenticated the client and checked its permission for this grant. Scopes a sign-in never
            // grants are refused before the code or password is spent, so a corrected request still succeeds.
            if (request.GetScopes().Except(OidcResults.SignInScopes).Any())
            {
                return OidcResults.Forbid(Errors.InvalidScope, "A sign-in grants only openid, email, profile and offline_access.");
            }

            return await headless.GrantAsync(request, cancellationToken);
        }

        if (request.IsClientCredentialsGrantType() && ApiKeys.IsKeyId(request.ClientId))
        {
            // An API key (TS-598) OpenIddict has authenticated: an organization or project token.
            return await platform.ApiKeyAsync(this.HttpContext, request, cancellationToken);
        }

        if (request.IsTokenExchangeGrantType())
        {
            // OpenIddict has validated the subject token and the client's token-exchange permission.
            return await platform.ExchangeAsync(this.HttpContext, request, cancellationToken);
        }

        if (request.IsClientCredentialsGrantType())
        {
            // OpenIddict has authenticated the client and checked its client-credentials permission. The client is the
            // subject; no scope is granted, so neither an identity nor a refresh token is issued.
            var service = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role)
                .SetClaim(Claims.Subject, request.ClientId)
                .SetResources(options.AccessTokenAudiences);
            service.SetDestinations(_ => [Destinations.AccessToken]);
            return Results.SignIn(new ClaimsPrincipal(service), null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        var principal = (await this.HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal
            ?? throw new InvalidOperationException("OpenIddict passed a token request without a principal.");
        var user = principal.GetClaim(Claims.Subject) is { } subject ? await users.GetAsync(OidcIssuer.InstanceId, subject, cancellationToken) : null;
        if (user is null || principal.GetClaim(OidcIssuer.EpochClaim) != user.AuthEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || !long.TryParse(principal.GetClaim(Claims.AuthenticationTime), System.Globalization.CultureInfo.InvariantCulture, out var authenticatedAt))
        {
            return OidcResults.Forbid(Errors.InvalidGrant, "The user is no longer signed in.");
        }

        return OidcResults.SignIn(user, DateTimeOffset.FromUnixTimeSeconds(authenticatedAt), principal.GetScopes(), OidcIssuer.SignInAudiences(options));
    }
}

/// <summary><c>GET /{slug}/oauth/userinfo</c>: claims about the user of a valid access token.</summary>
public sealed class UserInfoEndpoint(Users users) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/{slug}/oauth/userinfo");
        Name("oauth_userinfo");
        Summary("UserInfo endpoint");
        Description("Requires an access token issued by this issuer and not revoked. Identity tokens are rejected.");
        Tags("OAuth");
        Produces<UserInfoBody>();
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
    }

    /// <inheritdoc />
    public override async Task<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is null)
        {
            return Results.NotFound();
        }

        var principal = (await this.HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        if (principal?.GetClaim(Claims.Subject) is not { } subject || await users.GetAsync(OidcIssuer.InstanceId, subject, cancellationToken) is not { } user)
        {
            return Results.Challenge(OidcResults.Error(Errors.InvalidToken, "The access token no longer identifies a user."), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        var email = principal.HasScope(Scopes.Email);
        return Results.Ok(new UserInfoBody(user.Id, email ? user.Email : null, email ? true : null));
    }
}

/// <summary><c>POST /{slug}/oauth/revoke</c>: token revocation (RFC 7009), served by OpenIddict.</summary>
public sealed class RevocationEndpoint : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/{slug}/oauth/revoke");
        Name("oauth_revoke");
        Summary("Revocation endpoint");
        Description("RFC 7009, application/x-www-form-urlencoded token (and optionally token_type_hint); the client authenticates as at the token endpoint and may revoke only its own tokens. Revoking a refresh or access token ends its whole sign-in: every refresh token of it is refused and ts-auth stops accepting its access tokens, while access tokens validated offline (TerraBase) stay valid until they expire, at most 1 hour. An unknown, expired or already revoked token also answers 200.");
        Tags("OAuth");
        Accepts<Dictionary<string, string>>("application/x-www-form-urlencoded");
        Produces<Dictionary<string, object>>();
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    /// <inheritdoc />
    public override Task<IResult> HandleAsync(CancellationToken cancellationToken) => Task.FromResult(Results.NotFound());
}

/// <summary>
/// <c>GET /{slug}/oauth/logout</c>: RP-initiated logout. Revokes the hosted session and clears its cookie, then returns
/// to the client's registered <c>post_logout_redirect_uri</c> or shows a signed-out page.
/// </summary>
public sealed class LogoutEndpoint(Sessions sessions) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/{slug}/oauth/logout");
        Name("oauth_logout");
        Summary("End-session endpoint");
        Description("OpenID Connect RP-Initiated Logout 1.0. post_logout_redirect_uri must exactly match one registered for the client identified by client_id or id_token_hint. Access and refresh tokens already issued stay valid until they expire or are revoked.");
        Tags("OAuth");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async Task<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { } request)
        {
            return Results.NotFound();
        }

        await sessions.RevokeAsync(OidcIssuer.InstanceId, this.HttpContext.Request.Cookies[SecuritySchemes.SessionCookieName], cancellationToken);
        BrowserSecurity.DeleteSessionCookie(this.HttpContext.Response);
        return request.PostLogoutRedirectUri is null
            ? SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignedOut))
            : Results.SignOut(null, [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }
}

/// <summary>Sign-in and error results for the OpenIddict passthrough endpoints.</summary>
internal static class OidcResults
{
    public static AuthenticationProperties Error(string error, string description) => new(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
    });

    /// <summary>The only scopes a sign-in grants.</summary>
    public static readonly IReadOnlyList<string> SignInScopes = [Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.OfflineAccess];

    public static IResult Forbid(string error, string description) =>
        Results.Forbid(Error(error, description), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    /// <summary>
    /// Signs <paramref name="user"/> in with fresh claims. The access token always carries the email claims (TerraBase
    /// decision 0007 reads them) and every configured audience; the identity token carries them only with the
    /// <c>email</c> scope. The authentication epoch rides in codes and refresh tokens, so later grants can check it, and
    /// in the access token, so the platform management API (TS-598) refuses it once the user signed out everywhere.
    /// </summary>
    public static IResult SignIn(UserRecord user, DateTimeOffset authenticatedAt, IEnumerable<string> scopes, IReadOnlyList<string> audiences)
    {
        // TerraBase and platform scopes come only from the role-checked token exchange (TS-598), never with a sign-in,
        // although ts-dashboard holds their permissions for the exchange.
        if (scopes.Except(SignInScopes).Any())
        {
            return Forbid(Errors.InvalidScope, "A sign-in grants only openid, email, profile and offline_access.");
        }

        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role)
            .SetClaim(Claims.Subject, user.Id)
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.EmailVerified, true)
            .SetClaim(Claims.AuthenticationTime, authenticatedAt.ToUnixTimeSeconds())
            .SetClaim(OidcIssuer.EpochClaim, user.AuthEpoch)
            .SetScopes(scopes)
            .SetResources(audiences);
        var email = identity.HasScope(Scopes.Email);
        identity.SetDestinations(claim => claim.Type switch
        {
            Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
            Claims.Email or Claims.EmailVerified => email ? [Destinations.AccessToken, Destinations.IdentityToken] : [Destinations.AccessToken],
            Claims.AuthenticationTime => [Destinations.IdentityToken],
            OidcIssuer.EpochClaim => [Destinations.AccessToken],
            _ => [],
        });
        return Results.SignIn(new ClaimsPrincipal(identity), null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
