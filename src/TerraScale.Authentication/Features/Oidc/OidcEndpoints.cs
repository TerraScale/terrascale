using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using Microsoft.AspNetCore;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.DotNet.TerraBase;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Oidc;

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
        Description("OpenID Connect Discovery 1.0 metadata for the issuer {public origin}/{slug}: terrascale, or an existing project's id. Other slugs answer 404. A project tenant serves the authorization code flow for its own clients and end users, without device, client-credentials or token-exchange grants.");
        Tags("OAuth");
        Produces<Dictionary<string, object>>();
        Produces<OAuthErrorBody>(StatusCodes.Status404NotFound);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Results.NotFound());
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
        Description("Every ES256 key in the tenant's key ring (terrascale, or an existing project's id; others answer 404), so tokens signed before a rotation keep validating. A key is published before it signs.");
        Tags("OAuth");
        Produces<Dictionary<string, object>>();
        Produces<OAuthErrorBody>(StatusCodes.Status404NotFound);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Results.NotFound());
}

/// <summary>
/// <c>GET /{slug}/oauth/authorize</c>: the authorization endpoint with its parameters in the query string.
/// </summary>
public sealed class AuthorizeGetEndpoint(Sessions sessions, ProjectAuthSettings settings, TimeProvider clock, AuthOptions options)
    : AuthorizeEndpoint(sessions, settings, clock, options)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Path);
        Name("oauth_authorize");
        this.Describe();
    }
}

/// <summary>
/// <c>POST /{slug}/oauth/authorize</c>: the authorization endpoint with its parameters form-encoded (OpenID Connect Core
/// 3.1.2.1).
/// </summary>
public sealed class AuthorizePostEndpoint(Sessions sessions, ProjectAuthSettings settings, TimeProvider clock, AuthOptions options)
    : AuthorizeEndpoint(sessions, settings, clock, options)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(Path);
        Name("oauth_authorize_post");
        this.AcceptsForm();
        this.Describe();
    }
}

/// <summary>
/// The authorization endpoint (code flow with S256 PKCE), by GET or form POST. OpenIddict validates the client,
/// redirect URI, scopes and PKCE first; this signs in the user of the hosted session cookie, or sends the browser to the
/// hosted sign-in page and back.
/// </summary>
public abstract class AuthorizeEndpoint(Sessions sessions, ProjectAuthSettings settings, TimeProvider clock, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <summary>The endpoint's route.</summary>
    protected const string Path = "/{slug}/oauth/authorize";

    /// <summary>The resumed request's parameter: only a session created after these Unix milliseconds satisfies it.</summary>
    public const string LoginAfter = "ts_login_after";

    /// <summary>Documents the OpenID Connect form binding of a POST variant.</summary>
    protected void AcceptsForm() =>
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));

    /// <summary>The documentation both bindings share.</summary>
    protected void Describe()
    {
        Summary("Authorization endpoint");
        Description("GET with query parameters or POST with the same parameters as application/x-www-form-urlencoded. Authorization code flow (response_type=code) with PKCE code_challenge_method=S256 required. Without a live tsauth_session cookie the browser is redirected to /ui/{slug}/sign-in and returns here by GET with the same parameters. prompt=none answers login_required without a session or with one older than max_age. prompt=login, or a live session older than max_age, sends the browser to a deliberate sign-in: the resumed request drops login from prompt and carries ts_login_after (Unix milliseconds), which only a session created after that time satisfies in place of max_age, so it cannot be met by an older session. prompt=consent and select_account are not supported or advertised and answer invalid_request; prompt and ts_login_after may appear only once. Errors after client and redirect validation are redirected to the client. The response is a 302 to the client, or with response_mode=form_post a 200 page that posts the result to it."
            + " A project's configured session lifetime applies from the original authentication, including after rotation; the next session check persists any reduction, so a later increase cannot revive that session. A nonempty project redirect allowlist additionally requires an exact redirect_uri match; a disallowed URI receives no redirect, including error and form_post responses. An empty list keeps each client's registered URI validation."
            + " Sessions of active MFA users must come from a sign-in that proved the second factor; corrupt or unavailable factor state fails closed with temporarily_unavailable.");
        Tags("OAuth");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status404NotFound);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { } request)
        {
            return Results.NotFound();
        }

        var tenant = Tenancy.Current(this.HttpContext);
        if (!await OidcResults.AllowsRedirectAsync(settings, tenant, request.RedirectUri, cancellationToken))
        {
            // Do not send even an OAuth error to a redirect the project no longer permits.
            return OidcResults.DisallowedRedirect();
        }

        var parameters = HttpMethods.IsPost(this.HttpContext.Request.Method)
            ? (await this.HttpContext.Request.ReadFormAsync(cancellationToken)).ToList()
            : this.HttpContext.Request.Query.ToList();
        var marker = parameters.FirstOrDefault(parameter => parameter.Key == LoginAfter).Value;
        long? loginAfter = long.TryParse(marker, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) ? milliseconds : null;
        if (parameters.Any(parameter => parameter.Key is Parameters.Prompt or LoginAfter && parameter.Value.Count > 1) || marker.Count > 0 && loginAfter is null)
        {
            return OidcResults.Forbid(Errors.InvalidRequest, $"prompt and {LoginAfter} may appear only once, and {LoginAfter} must be Unix milliseconds.");
        }

        var cookie = Tenancy.SessionCookie(tenant);
        (SessionRecord Session, UserRecord User)? live;
        try
        {
            live = await sessions.ResolveAsync(tenant, this.HttpContext.Request.Cookies[cookie], cancellationToken);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status503ServiceUnavailable)
        {
            return OidcResults.Forbid(Errors.TemporarilyUnavailable, error.Message);
        }
        catch (TerraBaseUnavailableException)
        {
            // Storage was down (QA round 4, auth:11 answered 500): the client gets OAuth's retryable error instead.
            return OidcResults.Forbid(Errors.TemporarilyUnavailable, ApiException.Unavailable().Message);
        }

        var now = clock.GetUtcNow();
        var authenticatedAt = live?.Session.CreatedAt;
        // A resumed request's marker replaces max_age: the sign-in it asked for is recent enough, even for max_age=0.
        var stale = authenticatedAt is { } created && (loginAfter is { } after
            ? created.ToUnixTimeMilliseconds() <= after
            : request.MaxAge is { } maxAge && created.AddSeconds(maxAge) < now);
        var reauthenticate = request.HasPromptValue(PromptValues.Login);
        if (live is not { } signedIn || stale || reauthenticate)
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return OidcResults.Forbid(Errors.LoginRequired, live is null ? CredentialLineage.ConflictNotice : "The session is older than max_age allows.");
            }

            // Sign-in resumes the request as a GET without prompt=login; ts_login_after keeps the demand for a sign-in made
            // after this request, and the sign-in page never skips its form for such a request.
            var prompt = string.Join(' ', request.GetPromptValues().Remove(PromptValues.Login));
            var resume = parameters.Where(parameter => parameter.Key is not (Parameters.Prompt or LoginAfter)).ToList();
            if (prompt.Length > 0)
            {
                resume.Add(new(Parameters.Prompt, prompt));
            }

            if ((loginAfter ?? (stale || reauthenticate ? now.ToUnixTimeMilliseconds() : null)) is { } required)
            {
                resume.Add(new(LoginAfter, required.ToString(CultureInfo.InvariantCulture)));
            }

            var next = $"/{tenant}/oauth/authorize{QueryString.Create(resume)}";
            var notice = live is null && this.HttpContext.Request.Cookies.ContainsKey(cookie) ? "&notice=credential_changed" : string.Empty;
            return Results.Redirect($"/ui/{tenant}/sign-in?next={Uri.EscapeDataString(next)}{notice}");
        }

        return OidcResults.SignIn(signedIn.User, authenticatedAt!.Value, request.GetScopes(), OidcResults.Audiences(tenant, options, request.ClientId));
    }
}

/// <summary>
/// <c>POST /{slug}/oauth/token</c>: redeems an authorization code, rotates a refresh token, or issues a service client's
/// client-credentials access token. Each code and refresh token works once: a second presentation, even a concurrent
/// one in the same region, fails; after replication only the winning branch remains usable. Tokens are refused once the user signed out everywhere.
/// </summary>
public sealed class TokenEndpoint(TotpCredentials mfa, PlatformGrants platform, AuthOptions options, IOpenIddictApplicationStore<OidcApplication> applications,
    Sessions sessions, ProjectAuthSettings settings, TimeProvider clock, IOpenIddictTokenManager tokens) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/{slug}/oauth/token");
        Name("oauth_token");
        Summary("Token endpoint");
        Description("application/x-www-form-urlencoded grant_type=authorization_code (code, redirect_uri, code_verifier), grant_type=refresh_token (refresh_token), grant_type=urn:ietf:params:oauth:grant-type:device_code (device_code, client_id; RFC 8628 for the ts CLI, see /{slug}/oauth/device) or, for service clients only, grant_type=client_credentials. Every POST to /terrascale/oauth/token and /terrascale/oauth/device, including invalid client credentials and malformed requests, charges the socket peer's shared minute budget before client authentication: TS_AUTH_RATE_LIMIT (or --rate-limit), default 1000, per IPv4 address or IPv6 /64, with an additional IPv6 /48 budget at four times that capacity. Forwarding is disabled by default. With TS_AUTH_TRUSTED_PROXIES (or --trusted-proxies) configured, exact trusted socket peers have four times the aggregate peer capacity in separate trusted-proxy buckets and also charge a separate resolved-client budget of TS_AUTH_RATE_LIMIT per IPv4 address or IPv6 /64, with an additional resolved-client IPv6 /48 budget at four times that capacity; both apply before authentication. ASP.NET processes only the rightmost X-Forwarded-For hop from these peers. Missing or malformed rightmost addresses retain the proxy address and share its client budget. Other forwarding headers and submitted client identifiers do not select a budget. Exhaustion answers 429 rate_limited with Retry-After in seconds; missing peer addresses or unavailable limiter storage answer 503 temporarily_unavailable. Both are OAuth JSON errors with Cache-Control: no-store. Storage that does not answer within 10 seconds answers 503 temporarily_unavailable with Retry-After; a refresh_token retry after it may answer invalid_grant if the first attempt committed late, so sign in again then. Confidential clients authenticate with client_secret_basic or client_secret_post. Access tokens are ES256 JWTs with typ at+jwt whose aud holds TS_AUTH_ACCESS_TOKEN_AUDIENCES; a user's carry email and email_verified, a service client's have sub = client id and no scopes. Refresh tokens rotate on every use. Platform refresh tokens have a fixed expiry 14 days after the authorization code is redeemed."
            + " Project refresh tokens initially expire at the original auth_time plus the project's configured session lifetime (5 minutes to 30 days). Rotation never extends an observed expiry, and reducing policy immediately limits code and refresh redemption. Observing policy expiry revokes the whole sign-in, so later policy increases cannot revive its grants. Authorization-code redirect_uri must still satisfy the project's current nonempty allowlist as well as the client registration."
            + " Active MFA users' authorization-code, refresh and exchange grants need the ts_auth_mfa claim of a second-factor sign-in; primary-only ones answer invalid_grant. Pending and disabled factors permit primary authentication; corrupt or unavailable factor state answers 503 temporarily_unavailable. Machine client credentials and API keys are independent of user MFA. User sign-in access and identity tokens carry the server-issued numeric auth_time as Unix seconds; refresh and organization/project exchange preserve its original value. Submitted auth_time parameters are ignored. Exchange requires a nonnegative integer no more than 30 seconds ahead of the exchanging replica; missing, malformed, negative or further-ahead values answer invalid_grant. Machine tokens carry no auth_time. Authentication time alone does not establish MFA completion.");
        Tags("OAuth");
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<TokenBody>();
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<OAuthErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        this.HttpContext.Response.Headers.Pragma = "no-cache";
        try
        {
            return await this.GrantAsync(cancellationToken);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status503ServiceUnavailable)
        {
            return Results.Json(new OAuthErrorBody(Errors.TemporarilyUnavailable, error.Message), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task<IResult> GrantAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { } request)
        {
            return Results.NotFound();
        }

        var tenant = Tenancy.Current(this.HttpContext);
        if (Tenancy.IsProject(tenant) && !request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            // A project's end users sign in to its own applications; machine, device and platform grants are the platform's.
            return OidcResults.Forbid(Errors.UnsupportedGrantType, "A project issuer grants only authorization_code and refresh_token.");
        }

        if (request.IsAuthorizationCodeGrantType() && !await OidcResults.AllowsRedirectAsync(settings, tenant, request.RedirectUri, cancellationToken))
        {
            return OidcResults.Forbid(Errors.InvalidGrant, "The project does not permit this redirect URI.");
        }

        if (request.IsClientCredentialsGrantType())
        {
            var application = await applications.FindByClientIdAsync(request.ClientId!, cancellationToken);
            if (application is null)
            {
                return OidcResults.Forbid(Errors.InvalidClient, "The client is unknown, revoked or expired.");
            }

            if (application.ClientSecret?.StartsWith(ApiKeyApplicationManager.DigestPrefix, StringComparison.Ordinal) == true)
            {
                // An API key (TS-598) OpenIddict has authenticated: an organization or project token.
                return await platform.ApiKeyAsync(this.HttpContext, request, cancellationToken);
            }

            // OpenIddict authenticated this resolved service client and checked its client-credentials permission.
            var service = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role)
                .SetClaim(Claims.Subject, request.ClientId)
                .SetResources(options.AccessTokenAudiences);
            service.SetDestinations(_ => [Destinations.AccessToken]);
            return Results.SignIn(new ClaimsPrincipal(service), null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.GrantType == GitHubActionsGrant.GrantType)
        {
            // OpenIddict has authenticated the public client and checked its permission for this grant.
            var binding = await applications.FindByClientIdAsync(request.ClientId!, cancellationToken);
            return await platform.GitHubActionsAsync(request, binding, cancellationToken);
        }

        if (request.IsTokenExchangeGrantType())
        {
            // OpenIddict has validated the subject token and the client's token-exchange permission.
            return await platform.ExchangeAsync(this.HttpContext, request, cancellationToken);
        }

        var principal = (await this.HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal
            ?? throw new InvalidOperationException("OpenIddict passed a token request without a principal.");
        var user = principal.GetClaim(Claims.Subject) is { } subject
            ? await mfa.PrimaryUserAsync(tenant, subject, principal.GetClaim(OidcIssuer.EpochClaim), principal.GetClaim(OidcIssuer.StateClaim), principal.GetClaim(OidcIssuer.MfaClaim) is not null, cancellationToken)
            : null;
        if (user is null || ProjectSessionPolicy.ReadAuthenticationTime(principal, clock) is not { } authenticationTime)
        {
            return OidcResults.Forbid(Errors.InvalidGrant, "The user is no longer signed in.");
        }

        if (Tenancy.IsProject(tenant))
        {
            var lifetime = await sessions.GetLifetimeAsync(tenant, cancellationToken);
            if (authenticationTime > DateTimeOffset.MaxValue - lifetime || authenticationTime + lifetime <= clock.GetUtcNow())
            {
                await ProjectSessionPolicy.RevokeAsync(tokens, principal, cancellationToken);
                return OidcResults.Forbid(Errors.InvalidGrant, "The project's absolute session lifetime has elapsed.");
            }
        }

        return OidcResults.SignIn(user with { AuthenticationBindings = CredentialLineage.Read(principal) }, authenticationTime, principal.GetScopes(),
            OidcResults.Audiences(tenant, options, request.ClientId ?? principal.GetPresenters().FirstOrDefault()));
    }
}

/// <summary><c>GET /{slug}/oauth/userinfo</c>: the UserInfo endpoint by GET.</summary>
public sealed class UserInfoGetEndpoint(TotpCredentials mfa) : UserInfoEndpoint(mfa)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Path);
        Name(RouteName);
        this.Describe();
    }
}

/// <summary><c>POST /{slug}/oauth/userinfo</c>: the UserInfo endpoint by form POST (OpenID Connect Core 5.3.1).</summary>
public sealed class UserInfoPostEndpoint(TotpCredentials mfa) : UserInfoEndpoint(mfa)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(Path);
        Name(PostRouteName);
        this.AcceptsForm();
        this.Describe();
    }
}

/// <summary>The UserInfo endpoint: claims about the user of a valid access token.</summary>
public abstract class UserInfoEndpoint(TotpCredentials mfa) : EndpointWithoutRequest<IResult>
{
    internal const string RouteName = "oauth_userinfo";

    internal const string PostRouteName = "oauth_userinfo_post";

    /// <summary>The endpoint's route.</summary>
    protected const string Path = "/{slug}/oauth/userinfo";

    /// <summary>Documents the OpenID Connect form binding of a POST variant.</summary>
    protected void AcceptsForm() =>
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));

    /// <summary>The documentation both bindings share.</summary>
    protected void Describe()
    {
        Summary("UserInfo endpoint");
        Description("GET, or POST with an application/x-www-form-urlencoded body. Requires an access token issued by this issuer and not revoked, with the user's current authentication epoch. Tokens of active MFA accounts without the ts_auth_mfa claim and identity tokens are rejected. Unavailable or corrupt MFA state answers 503 temporarily_unavailable.");
        Tags("OAuth");
        Produces<UserInfoBody>();
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<OAuthErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await this.ClaimsAsync(cancellationToken);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status503ServiceUnavailable)
        {
            return Results.Json(new OAuthErrorBody(Errors.TemporarilyUnavailable, error.Message), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task<IResult> ClaimsAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is null)
        {
            return Results.NotFound();
        }

        var principal = (await this.HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        if (principal?.GetClaim(Claims.Subject) is not { } subject
            || await mfa.PrimaryUserAsync(Tenancy.Current(this.HttpContext), subject, principal.GetClaim(OidcIssuer.EpochClaim), principal.GetClaim(OidcIssuer.StateClaim),
                principal.GetClaim(OidcIssuer.MfaClaim) is not null, cancellationToken) is not { } user)
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
        Description("RFC 7009, application/x-www-form-urlencoded token (and optionally token_type_hint); the client authenticates as at the token endpoint and may revoke only its own tokens. Revoking a refresh or access token ends its whole sign-in: every refresh token of it is refused and ts-auth stops accepting its access tokens, while access tokens validated offline (TerraBase) stay valid until they expire, at most 5 minutes. An unknown, expired or already revoked token also answers 200.");
        Tags("OAuth");
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<Dictionary<string, object>>();
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Results.NotFound());
}

/// <summary><c>GET /{slug}/oauth/logout</c>: RP-initiated logout with its parameters in the query string.</summary>
public sealed class LogoutGetEndpoint(Sessions sessions, ProjectAuthSettings settings, AuthOptions options) : LogoutEndpoint(sessions, settings, options)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Path);
        Name("oauth_logout");
        this.Describe();
    }
}

/// <summary><c>POST /{slug}/oauth/logout</c>: RP-initiated logout with its parameters form-encoded (RP-Initiated Logout 1.0 section 2).</summary>
public sealed class LogoutPostEndpoint(Sessions sessions, ProjectAuthSettings settings, AuthOptions options) : LogoutEndpoint(sessions, settings, options)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(Path);
        Name("oauth_logout_post");
        this.AcceptsForm();
        this.Describe();
    }
}

/// <summary>
/// RP-initiated logout, by GET or form POST. Revokes the hosted session and clears its cookie, then shows the signed-out
/// page, which continues to the client's registered <c>post_logout_redirect_uri</c> when there is one. A live session
/// is ended only for an <c>id_token_hint</c> issued to its user or once the user confirms on the hosted page
/// (RP-Initiated Logout 1.0 section 2): anything else shows the confirmation and changes nothing.
/// </summary>
public abstract class LogoutEndpoint(Sessions sessions, ProjectAuthSettings settings, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <summary>The endpoint's route.</summary>
    protected const string Path = "/{slug}/oauth/logout";

    /// <summary>The form field the hosted confirmation posts back; honored only from a same-origin POST.</summary>
    public const string ConfirmField = "confirm";

    /// <summary>The request parameters the confirmation carries back, so the confirmed request is validated as the first.</summary>
    private static readonly string[] Carried = [Parameters.IdTokenHint, Parameters.ClientId, Parameters.PostLogoutRedirectUri, Parameters.State, Parameters.UiLocales, "logout_hint"];

    /// <summary>Documents the OpenID Connect form binding of a POST variant.</summary>
    protected void AcceptsForm() =>
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));

    /// <summary>The documentation both bindings share.</summary>
    protected void Describe()
    {
        Summary("End-session endpoint");
        Description("OpenID Connect RP-Initiated Logout 1.0, by GET with query parameters or POST with the same parameters as application/x-www-form-urlencoded. post_logout_redirect_uri must exactly match one registered for the client identified by client_id or id_token_hint and, for a project with a nonempty redirect allowlist, an entry in that list. A disallowed project URI answers a local error without navigation. The answer is otherwise the hosted Signed out page; with a validated post_logout_redirect_uri it continues there (with state, if sent) through a Continue link and a 2-second meta refresh. A live hosted session ends only when id_token_hint was issued to its user or the user confirms: otherwise the answer is a Sign out? page whose same-origin form POST (confirm=1, carrying the request's parameters) ends it, and the request itself changes nothing. Access and refresh tokens already issued stay valid until they expire or are revoked.");
        Tags("OAuth");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { } request)
        {
            return Results.NotFound();
        }

        var tenant = Tenancy.Current(this.HttpContext);
        if (!await OidcResults.AllowsRedirectAsync(settings, tenant, request.PostLogoutRedirectUri, cancellationToken))
        {
            return OidcResults.DisallowedRedirect();
        }

        var cookie = this.HttpContext.Request.Cookies[Tenancy.SessionCookie(tenant)];
        if (await sessions.ResolveAsync(tenant, cookie, cancellationToken) is { } live && !await this.ConfirmedAsync(request, live.User.Id))
        {
            // Names no account, so a page opened by another site learns nothing it could not already.
            var fields = Carried.Select(name => (Name: name, Value: (string?)request[name]))
                .Where(field => !string.IsNullOrEmpty(field.Value))
                .Select(field => KeyValuePair.Create(field.Name, field.Value!))
                .ToList();
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignOutConfirm, tenant) { LogoutFields = fields });
        }

        await sessions.RevokeAsync(tenant, cookie, cancellationToken);
        BrowserSecurity.DeleteSessionCookie(this.HttpContext.Response, tenant);
        // OpenIddict has validated post_logout_redirect_uri against the client's registration before this runs. The page
        // navigates there itself, so the user sees that the sign-out happened.
        var returnTo = request.PostLogoutRedirectUri is { } uri
            ? (string.IsNullOrEmpty(request.State) ? uri : Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(uri, Parameters.State, request.State))
            : null;
        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignedOut, tenant) { ReturnTo = returnTo });
    }

    /// <summary>
    /// Whether the request may end the session of <paramref name="userId"/>: its <c>id_token_hint</c>, which OpenIddict
    /// has validated for this issuer and client, names that user, or it is the hosted confirmation posted from the public
    /// origin. A cross-site page can make the browser post, but not with this origin.
    /// </summary>
    private async Task<bool> ConfirmedAsync(OpenIddictRequest request, string userId)
    {
        var context = this.HttpContext;
        if (HttpMethods.IsPost(context.Request.Method) && (string?)request[ConfirmField] == "1"
            && context.Request.Headers.Origin is [{ } origin] && SignInFlow.IsPublicOrigin(origin, options.PublicOrigin))
        {
            return true;
        }

        return request.IdTokenHint is not null
            && (await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal?.GetClaim(Claims.Subject) == userId;
    }
}

/// <summary>Sign-in and error results for the OpenIddict passthrough endpoints.</summary>
internal static class OidcResults
{
    public static IResult DisallowedRedirect() => Results.Json(new OAuthErrorBody(Errors.InvalidRequest,
        "The project does not permit this redirect URI."), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>The project allowlist narrows, and never replaces, OpenIddict's client URI validation.</summary>
    public static async Task<bool> AllowsRedirectAsync(ProjectAuthSettings settings, string tenant, string? uri, CancellationToken cancellationToken)
    {
        if (!Tenancy.IsProject(tenant) || uri is null)
        {
            return true;
        }

        var allowed = (await settings.GetAsync(tenant, cancellationToken).ConfigureAwait(false)).Configuration?.RedirectUrls;
        return allowed is not { Count: > 0 } || allowed.Contains(uri, StringComparer.Ordinal);
    }

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
    /// The audiences of a sign-in access token: the platform's (<see cref="OidcIssuer.SignInAudiences"/>), or for a
    /// project tenant only the client it was issued to, so no TerraScale API accepts it (project-tenant-management.md).
    /// </summary>
    public static IReadOnlyList<string> Audiences(string tenant, AuthOptions options, string? clientId) =>
        !Tenancy.IsProject(tenant) ? OidcIssuer.SignInAudiences(options)
        : clientId is { Length: > 0 } ? [clientId]
        : throw new InvalidOperationException("A project sign-in has no client.");

    /// <summary>The access-token claim naming a platform role.</summary>
    public const string PlatformRoleClaim = "platform_role";

    /// <summary>The platform role of a platform admin.</summary>
    public const string PlatformRoleAdmin = "admin";

    /// <summary>
    /// Signs <paramref name="user"/> in with fresh claims. The access token always carries the email claims (TerraBase
    /// decision 0007 reads them) and every configured audience; the identity token carries them only with the
    /// <c>email</c> scope. The authentication epoch rides in codes and refresh tokens, so later grants can check it, and
    /// in the access token, so the platform management API (TS-598) refuses it once the user signed out everywhere.
    /// The server-issued authentication time also rides in user access tokens; refresh never renews it. A platform admin's
    /// access token also carries <see cref="PlatformRoleClaim"/>, read from the stored user at every sign-in and refresh; the
    /// platform owner (<see cref="PlatformBootstrap.OwnerEmail"/>) always carries it.
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
            .SetClaim(OidcIssuer.StateClaim, user.AuthStamp)
            .SetClaim(OidcIssuer.MfaClaim, user.MultiFactor ? "totp" : null)
            .SetScopes(scopes)
            .SetResources(audiences);
        // Only the platform's own tenant has platform admins; TerraBase's explore API reads the claim (platform-databases.md).
        // The platform owner is always one, in code, so a wiped and re-bootstrapped store needs no operator write; every
        // sign-in proves control of the mailbox first.
        if (user.InstanceId == OidcIssuer.InstanceId && (user.PlatformRole == PlatformRoleAdmin || user.Email == PlatformBootstrap.OwnerEmail))
        {
            identity.SetClaim(PlatformRoleClaim, PlatformRoleAdmin);
        }

        var email = identity.HasScope(Scopes.Email);
        identity.SetDestinations(claim => claim.Type switch
        {
            Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
            Claims.Email or Claims.EmailVerified => email ? [Destinations.AccessToken, Destinations.IdentityToken] : [Destinations.AccessToken],
            Claims.AuthenticationTime => [Destinations.AccessToken, Destinations.IdentityToken],
            OidcIssuer.EpochClaim or OidcIssuer.StateClaim or OidcIssuer.MfaClaim or PlatformRoleClaim => [Destinations.AccessToken],
            _ => [],
        });
        var principal = new ClaimsPrincipal(identity);
        CredentialLineage.Attach(principal, user.AuthenticationBindings);
        return Results.SignIn(principal, null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
