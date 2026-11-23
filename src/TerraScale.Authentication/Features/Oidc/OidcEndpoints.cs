using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Agents;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Impersonation;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.ProjectOrganizations;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet.TerraBase;
using TerraScale.Endpoints;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Oidc;

public sealed record OAuthErrorBody(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

public sealed record TokenBody(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken);

public sealed record TokenRequestForm(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("client_id")] string? ClientId = null,
    [property: JsonPropertyName("client_secret")] string? ClientSecret = null,
    [property: JsonPropertyName("code")] string? Code = null,
    [property: JsonPropertyName("redirect_uri")] string? RedirectUri = null,
    [property: JsonPropertyName("code_verifier")] string? CodeVerifier = null,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken = null,
    [property: JsonPropertyName("device_code")] string? DeviceCode = null,
    [property: JsonPropertyName("subject_token")] string? SubjectToken = null,
    [property: JsonPropertyName("subject_token_type")] string? SubjectTokenType = null,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("resource")] string? Resource = null,
    [property: JsonPropertyName("ts_refresh_expires_at")] long? RefreshExpiresAt = null,
    [property: JsonPropertyName("assertion")] string? Assertion = null,
    [property: JsonPropertyName("requested_subject")] string? RequestedSubject = null);

public sealed record UserInfoBody(
    [property: JsonPropertyName("sub")] string Subject,
    [property: JsonPropertyName("email"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email,
    [property: JsonPropertyName("email_verified"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? EmailVerified,
    [property: JsonPropertyName("given_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FirstName,
    [property: JsonPropertyName("family_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastName,
    [property: JsonPropertyName("picture"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageUrl,
    [property: JsonPropertyName("public_metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? PublicMetadata,
    [property: JsonPropertyName("act"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImpersonationActor? Actor = null,
    [property: JsonPropertyName("is_anonymous"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsAnonymous = null);

public sealed class DiscoveryEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/{slug}/.well-known/openid-configuration");
        Name("oidc_discovery");
        Summary("OpenID Provider metadata");
        Description("OpenID Connect Discovery 1.0 metadata for the issuer {public origin}/{slug}: terrascale, or an existing project's id. Other slugs answer 404. A project tenant serves authorization-code and refresh flows for its interactive clients and end users, and client-credentials grants for its machine apps. Device and token-exchange grants are platform-only.");
        Tags("OAuth");
        Produces<Dictionary<string, object>>();
        Produces<OAuthErrorBody>(StatusCodes.Status404NotFound);
        AllowAnonymous();
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IResult>(TypedResults.NotFound());
}

public sealed class JwksEndpoint : EndpointWithoutRequest<IResult>
{
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

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IResult>(TypedResults.NotFound());
}

public sealed class AuthorizeGetEndpoint(Impersonations impersonations, Sessions sessions, ProjectAuthSettings settings, TimeProvider clock, AuthOptions options)
    : AuthorizeEndpoint(impersonations, sessions, settings, clock, options)
{
    public override void Configure()
    {
        Get(Path);
        Name("oauth_authorize");
        this.Describe();
    }
}

public sealed class AuthorizePostEndpoint(Impersonations impersonations, Sessions sessions, ProjectAuthSettings settings, TimeProvider clock, AuthOptions options)
    : AuthorizeEndpoint(impersonations, sessions, settings, clock, options)
{
    public override void Configure()
    {
        Post(Path);
        Name("oauth_authorize_post");
        this.AcceptsForm();
        this.Describe();
    }
}

public abstract class AuthorizeEndpoint(Impersonations impersonations, Sessions sessions, ProjectAuthSettings settings, TimeProvider clock, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    protected const string Path = "/{slug}/oauth/authorize";

    public const string LoginAfter = "ts_login_after";

    protected void AcceptsForm() =>
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));

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

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { } request)
        {
            return TypedResults.NotFound();
        }

        var tenant = Tenancy.Current(this.HttpContext);
        if (!await OidcResults.AllowsRedirectAsync(settings, tenant, request.RedirectUri, cancellationToken))
        {
            return OidcResults.DisallowedRedirect();
        }

        var parameters = HttpMethods.IsPost(this.HttpContext.Request.Method)
            ? (await this.HttpContext.Request.ReadFormAsync(cancellationToken)).ToList()
            : this.HttpContext.Request.Query.ToList();
        var marker = parameters.FirstOrDefault(parameter => parameter.Key == LoginAfter).Value;
        long? loginAfter = long.TryParse(marker, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) ? milliseconds : null;
        if (parameters.Any(parameter => parameter.Key is Parameters.Prompt or LoginAfter && parameter.Value.Count > 1) || marker.Count > 0 && loginAfter is null)
        {
            return OidcResults.Forbid(OidcErrors.InvalidRequest, $"prompt and {LoginAfter} may appear only once, and {LoginAfter} must be Unix milliseconds.");
        }

        var ticketParameter = parameters.FirstOrDefault(parameter => parameter.Key == "impersonation_ticket").Value;
        if (ticketParameter.Count > 0)
        {
            if (ticketParameter.Count != 1 || string.IsNullOrEmpty(ticketParameter[0]))
            {
                return OidcResults.Forbid(OidcErrors.InvalidRequest, "Supply exactly one impersonation ticket.");
            }

            try
            {
                var admin = await sessions.ResolveVersionedAsync(OidcIssuer.InstanceId,
                    this.HttpContext.Request.Cookies[Tenancy.SessionCookie(OidcIssuer.InstanceId)], cancellationToken);
                var redeemed = await impersonations.RedeemAsync(tenant, request.ClientId, ticketParameter[0]!, admin?.User,
                    admin is { } liveAdmin ? Sessions.Live(liveAdmin.Session, liveAdmin.Version, clock.GetUtcNow()) : null, cancellationToken);
                this.HttpContext.Response.Cookies.Append(Impersonations.Cookie(tenant), redeemed.Cookie, ImpersonationBrowser.CookieOptions(redeemed.Live.Record.ExpiresAt));
                this.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
                return OidcResults.SignIn(options, redeemed.Live.User, redeemed.Live.Record.RedeemedAt!.Value, request.GetScopes(),
                    OidcResults.Audiences(tenant, options, request.ClientId), impersonation: redeemed.Live.Record);
            }
            catch (ApiException error) when (error.Status == 400)
            {
                return OidcResults.Forbid(OidcErrors.InvalidRequest, error.Message);
            }
        }

        if (this.HttpContext.Request.Cookies.TryGetValue(Impersonations.Cookie(tenant), out var actingCookie))
        {
            var acting = await impersonations.ResolveAsync(tenant, actingCookie, cancellationToken);
            return acting is not null && acting.Record.ClientId == request.ClientId
                ? OidcResults.SignIn(options, acting.User, acting.Record.RedeemedAt!.Value, request.GetScopes(),
                    OidcResults.Audiences(tenant, options, request.ClientId), impersonation: acting.Record)
                : OidcResults.Forbid(OidcErrors.LoginRequired, "End the impersonation before signing in to another client.");
        }

        var cookie = Tenancy.SessionCookie(tenant);
        (SessionRecord Session, UserRecord User)? live;
        try
        {
            live = await sessions.ResolveAsync(tenant, this.HttpContext.Request.Cookies[cookie], cancellationToken);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status503ServiceUnavailable)
        {
            return OidcResults.Forbid(OidcErrors.TemporarilyUnavailable, error.Message);
        }
        catch (TerraBaseUnavailableException)
        {
            return OidcResults.Forbid(OidcErrors.TemporarilyUnavailable, ApiException.Unavailable().Message);
        }

        var now = clock.GetUtcNow();
        var authenticatedAt = live?.Session.VerifiedAt ?? live?.Session.CreatedAt;
        var stale = authenticatedAt is { } created && (loginAfter is { } after
            ? created.ToUnixTimeMilliseconds() <= after
            : request.MaxAge is { } maxAge && created.AddSeconds(maxAge) < now);
        var reauthenticate = request.HasPromptValue(PromptValues.Login);
        if (live is not { } signedIn || stale || reauthenticate)
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return OidcResults.Forbid(OidcErrors.LoginRequired, live is null ? CredentialLineage.ConflictNotice : "The session is older than max_age allows.");
            }

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
            var locales = request.UiLocales is { Length: > 0 } requested ? "&ui_locales=" + Uri.EscapeDataString(requested) : string.Empty;
            return TypedResults.Redirect($"/ui/{tenant}/sign-in?next={Uri.EscapeDataString(next)}{notice}{locales}");
        }

        OrganizationClaims? organization = null;
        if (Tenancy.IsProject(tenant))
        {
            var organizations = this.HttpContext.RequestServices.GetRequiredService<ProjectOrganizationDirectory>();
            if (parameters.Any(parameter => parameter.Key == "organization" && parameter.Value.Count != 1))
            {
                return OidcResults.Forbid(OidcErrors.InvalidRequest, "organization must appear once.");
            }

            var suggestions = request.HasPromptValue(PromptValues.None) ? [] : await organizations.AutoJoinAsync(signedIn.User, cancellationToken);
            var selected = (string?)request.GetParameter("organization");
            if (selected is not null)
            {
                organization = await organizations.ClaimsAsync(tenant, selected, signedIn.User.Id, cancellationToken);
                if (organization is null)
                {
                    return OidcResults.Forbid(OidcErrors.AccessDenied, "The user is not a member of this organization.");
                }
            }
            else
            {
                var choices = await organizations.ChoicesAsync(tenant, signedIn.User.Id, cancellationToken);
                if (choices.Count > 1 || suggestions.Count > 0)
                {
                    if (request.HasPromptValue(PromptValues.None))
                    {
                        return OidcResults.Forbid(OidcErrors.InteractionRequired, "Choose an organization.");
                    }

                    var resume = $"/{tenant}/oauth/authorize{QueryString.Create(parameters)}";
                    return OrganizationPageModel.Render(this.HttpContext, new(tenant, "", "Choose an organization", Next: resume,
                        Choices: [.. choices.Select(choice => new OrganizationChoice(choice.Slug, choice.Role,
                            $"/{tenant}/oauth/authorize{QueryString.Create(parameters.Where(parameter => parameter.Key != "organization").Append(new("organization", choice.Id)))}"))],
                        Suggestions: suggestions));
                }

                organization = choices.SingleOrDefault();
            }
        }

        return OidcResults.SignIn(options, signedIn.User, authenticatedAt!.Value, request.GetScopes(), OidcResults.Audiences(tenant, options, request.ClientId), organization: organization,
            organizationPicker: await OidcResults.OrganizationPickerAsync(settings, tenant, request.ClientId, cancellationToken));
    }
}

public sealed class TokenEndpoint(Impersonations impersonations, TotpCredentials mfa, PlatformGrants platform, AuthOptions options, IOpenIddictApplicationStore<OidcApplication> applications,
    Sessions sessions, ProjectAuthSettings settings, TimeProvider clock, IOpenIddictTokenManager tokens, AgentTokens agents, AgentExchange exchange) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/{slug}/oauth/token");
        Name("oauth_token");
        Summary("Token endpoint");
        Description("application/x-www-form-urlencoded grant_type=authorization_code (code, redirect_uri, code_verifier), grant_type=refresh_token (refresh_token), grant_type=urn:ietf:params:oauth:grant-type:device_code (device_code, client_id; RFC 8628 for the ts CLI, see /{slug}/oauth/device) or, for service clients only, grant_type=client_credentials. Every POST to /terrascale/oauth/token and /terrascale/oauth/device, including invalid client credentials and malformed requests, charges the socket peer's shared minute budget before client authentication: TS_AUTH_RATE_LIMIT (or --rate-limit), default 1000, per IPv4 address or IPv6 /64, with an additional IPv6 /48 budget at four times that capacity. Forwarding is disabled by default. With TS_AUTH_TRUSTED_PROXIES (or --trusted-proxies) configured, exact trusted socket peers have four times the aggregate peer capacity in separate trusted-proxy buckets and also charge a separate resolved-client budget of TS_AUTH_RATE_LIMIT per IPv4 address or IPv6 /64, with an additional resolved-client IPv6 /48 budget at four times that capacity; both apply before authentication. ASP.NET processes only the rightmost X-Forwarded-For hop from these peers. Missing or malformed rightmost addresses retain the proxy address and share its client budget. Other forwarding headers and submitted client identifiers do not select a budget. Exhaustion answers 429 rate_limited with Retry-After in seconds; missing peer addresses or unavailable limiter storage answer 503 temporarily_unavailable. Both are OAuth JSON errors with Cache-Control: no-store. Storage that does not answer within 10 seconds answers 503 temporarily_unavailable with Retry-After; a refresh_token retry after it may answer invalid_grant if the first attempt committed late, so sign in again then. Confidential clients authenticate with client_secret_basic or client_secret_post; public clients (authorization_code with PKCE, device_code) send only client_id, and the agent jwt-bearer grant sends no client authentication at all. Access tokens are ES256 JWTs with typ at+jwt whose aud holds TS_AUTH_ACCESS_TOKEN_AUDIENCES; a user's carry email and email_verified, a service client's have sub = client id and no scopes. Refresh tokens rotate on every use, with one exception: when the ts-dashboard client redeems an authorization code with ts_refresh_expires_at (Unix seconds, after now and at most 365 days and 5 minutes ahead, the 5 minutes absorbing a clock difference between the dashboard and ts-auth), the grant's refresh token expires at that instant and never rotates, so a refresh answers no refresh_token and the client keeps using the one it has, authenticating as the client every time; any other ts_refresh_expires_at from that client answers invalid_request and leaves the code unredeemed. Other clients, other grants and refresh requests ignore the parameter. Other platform refresh tokens have a fixed expiry 14 days after the authorization code is redeemed."
            + " Project refresh tokens initially expire at the original auth_time plus the project's configured session lifetime (5 minutes to 30 days). Rotation never extends an observed expiry, and reducing policy immediately limits code and refresh redemption. Observing policy expiry revokes the whole sign-in, so later policy increases cannot revive its grants. Authorization-code redirect_uri must still satisfy the project's current nonempty allowlist as well as the client registration."
            + " Active MFA users' authorization-code, refresh and exchange grants need the ts_auth_mfa claim of a second-factor sign-in; primary-only ones answer invalid_grant. Pending and disabled factors permit primary authentication; corrupt or unavailable factor state answers 503 temporarily_unavailable. Machine client credentials and API keys are independent of user MFA. User sign-in access and identity tokens carry the server-issued numeric auth_time as Unix seconds; refresh and organization/project exchange preserve its original value. Submitted auth_time parameters are ignored. Exchange requires a nonnegative integer no more than 30 seconds ahead of the exchanging replica; missing, malformed, negative or further-ahead values answer invalid_grant. Machine tokens carry no auth_time. Authentication time alone does not establish MFA completion."
            + " Project machine apps use client_credentials with their own nonempty allowed scopes, an optional resource from allowed_audiences, and a per-app 5 to 60 minute lifetime (default 15). Their sub is client_id; aud is the selected resource. They have no end-user identity. A machine client has an additional 1000/minute budget. Interactive apps cannot use client_credentials."
            + " Agents of a project, and agents of TerraScale accounts at /terrascale/oauth/token (allowed scopes mcp:read and mcp:write), use grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer (RFC 7523) without client authentication: assertion is an ES256 JWT signed by an active key of an active agent, header kid = the key's kid and no jwk, jku, x5u, x5c or crit member, iss = sub = the agent id, aud = exactly this token endpoint URL, integer iat and exp (no fraction, exponent or string) with exp at most 300 seconds after iat (30 seconds of clock skew) and a jti of at most 128 characters used once per agent. Optional scope is a subset of the agent's allowed_scopes (default all of them) and optional requested_subject is owner (default) or agent (only for an agent with allow_self_subject); any other parameter answers invalid_request. The answer has access_token, token_type Bearer, expires_in 300 (less when the agent's expires_at comes sooner; the token never outlives the agent) and scope, and no refresh or ID token. The token has token_use agent, actor_type agent, agent_id and scope, no aud, and either sub = the owner with act.sub = the agent, or sub = the agent with owner_sub = the owner. Every refusal (unknown or revoked key, bad signature, algorithm, header, issuer, audience or times, replayed jti, revoked or expired agent, owner disabled, deleted or signed out everywhere since the agent was created, scope outside allowed_scopes, self subject not allowed) answers the same 400 invalid_grant. Each agent has 60 tokens a minute and each owner's agents 600, charged only for requests that pass every check but the final save. The token is released only when one save commits the used jti (kept 10 minutes past the assertion's accepted window), the agent's last_used_at when due, and the versions of the key and owner it checked; a revocation, sign-out everywhere or disable committed before that save makes it fail."
            + " Only the ts-dashboard client, authenticated with its secret, may use grant_type=urn:terrascale:params:oauth:grant-type:agent-exchange at /terrascale/oauth/token, with subject_token (a delegated agent token of the platform issuer: token_use agent, act.sub = agent_id, sub = the owner, no aud, unexpired) and subject_token_type urn:ietf:params:oauth:token-type:access_token and nothing else. ts-auth checks the agent online (active, unexpired, an active key, the owner enabled and not signed out everywhere since the agent was created) and answers an access token for the owner: the issuer audiences, agent_id and act.sub = the agent, auth_time = the agent's creation, no ts_auth_mfa, no scopes, no refresh or ID token, and expires_in at most 300 seconds and never past the subject token. Every use of it (platform API, userinfo, organization or project exchange) reads the agent again from the home region and refuses it once the agent is revoked or its owner signed out. Missing or failed client authentication answers 401 invalid_client; another client answers 400 unauthorized_client; another parameter, another subject_token_type or no subject_token answers 400 invalid_request; every refusal of the subject token or its agent answers 400 invalid_grant.");
        Tags("OAuth");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(TokenRequestForm)));
        Produces<TokenBody>();
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<OAuthErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic, PublicClientAlternative: true));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        this.HttpContext.Response.Headers.Pragma = "no-cache";
        try
        {
            return await this.GrantAsync(cancellationToken);
        }
        catch (Exception error) when (AuthData.IsUnavailable(error) && !this.HttpContext.RequestAborted.IsCancellationRequested)
        {
            var unavailable = error as ApiException ?? ApiException.Unavailable(error);
            return TypedResults.Json(new OAuthErrorBody(OidcErrors.TemporarilyUnavailable, unavailable.Message), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task<IResult> GrantAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { } request)
        {
            return TypedResults.NotFound();
        }

        var tenant = Tenancy.Current(this.HttpContext);
        if (request.GrantType == AgentTokens.GrantType)
        {
            return await agents.GrantAsync(tenant, request, cancellationToken);
        }

        if (Tenancy.IsProject(tenant) && !request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType() && !request.IsClientCredentialsGrantType())
        {
            return OidcResults.Forbid(OidcErrors.UnsupportedGrantType, "A project issuer grants authorization_code, refresh_token, machine client_credentials and the agent jwt-bearer grant.");
        }

        if (request.GrantType == AgentExchange.GrantType)
        {
            return await exchange.ExchangeAsync(this.HttpContext, request, cancellationToken);
        }

        if (request.IsAuthorizationCodeGrantType() && !await OidcResults.AllowsRedirectAsync(settings, tenant, request.RedirectUri, cancellationToken))
        {
            return OidcResults.Forbid(OidcErrors.InvalidGrant, "The project does not permit this redirect URI.");
        }

        if (request.IsClientCredentialsGrantType())
        {
            var application = await applications.FindByClientIdAsync(request.ClientId!, cancellationToken);
            if (application is null)
            {
                return OidcResults.Forbid(OidcErrors.InvalidClient, "The client is unknown, revoked or expired.");
            }

            if (Tenancy.IsProject(tenant))
            {
                return await MachineClients.GrantAsync(this.HttpContext, request, application, cancellationToken);
            }

            if (application.ClientSecret?.StartsWith(ApiKeyApplicationManager.DigestPrefix, StringComparison.Ordinal) == true)
            {
                return await platform.ApiKeyAsync(this.HttpContext, request, cancellationToken);
            }

            var service = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role)
                .SetClaim(Claims.Subject, request.ClientId)
                .SetResources(options.AccessTokenAudiences);
            service.SetDestinations(_ => [Destinations.AccessToken]);
            return TypedResults.SignIn(new ClaimsPrincipal(service), null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.GrantType == GitHubActionsGrant.GrantType)
        {
            var binding = await applications.FindByClientIdAsync(request.ClientId!, cancellationToken);
            return await platform.GitHubActionsAsync(request, binding, cancellationToken);
        }

        if (request.IsTokenExchangeGrantType())
        {
            return await platform.ExchangeAsync(this.HttpContext, request, cancellationToken);
        }

        var principal = (await this.HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal
            ?? throw new InvalidOperationException("OpenIddict passed a token request without a principal.");
        var acting = principal.GetClaim(Impersonations.Claim) is { } actingId
            ? await impersonations.FindAsync(tenant, actingId, cancellationToken) : null;
        var user = principal.HasClaim(claim => claim.Type == Impersonations.Claim)
            ? acting is not null && acting.Record.ClientId == request.ClientId && acting.User.Id == principal.GetClaim(Claims.Subject) ? acting.User : null
            : principal.GetClaim(Claims.Subject) is { } subject
                ? await mfa.PrimaryUserAsync(tenant, subject, principal.GetClaim(OidcIssuer.EpochClaim), principal.GetClaim(OidcIssuer.StateClaim), principal.GetClaim(OidcIssuer.MfaClaim), cancellationToken)
                : null;
        if (user is null || ProjectSessionPolicy.ReadAuthenticationTime(principal, clock) is not { } authenticationTime)
        {
            return OidcResults.Forbid(OidcErrors.InvalidGrant, "The user is no longer signed in.");
        }

        if (Tenancy.IsProject(tenant) && principal.GetClaim("ts_auth_session") is { } sessionId)
        {
            if (await sessions.ResolveIdAsync(tenant, user.Id, sessionId, cancellationToken) is not { } live)
            {
                await ProjectSessionPolicy.RevokeAsync(tokens, principal, cancellationToken);
                return OidcResults.Forbid(OidcErrors.InvalidGrant, "The project session is no longer active.");
            }

            user = live.User;
            authenticationTime = live.Session.VerifiedAt ?? authenticationTime;
            var lifetime = await sessions.GetLifetimeAsync(tenant, cancellationToken);
            if (authenticationTime > DateTimeOffset.MaxValue - lifetime || authenticationTime + lifetime <= clock.GetUtcNow())
            {
                await ProjectSessionPolicy.RevokeAsync(tokens, principal, cancellationToken);
                return OidcResults.Forbid(OidcErrors.InvalidGrant, "The project's absolute session lifetime has elapsed.");
            }
        }

        if (!FixedRefreshTokens.TryRead(request, tenant, clock.GetUtcNow(), out var refreshExpiresAt))
        {
            return OidcResults.Forbid(OidcErrors.InvalidRequest, FixedRefreshTokens.InvalidNotice);
        }

        OrganizationClaims? organization = null;
        if (principal.GetClaim("org_id") is { } orgId)
        {
            organization = await this.HttpContext.RequestServices.GetRequiredService<ProjectOrganizationDirectory>().ClaimsAsync(tenant, orgId, user.Id, cancellationToken);
            if (organization is null)
            {
                return OidcResults.Forbid(OidcErrors.InvalidGrant, "The organization membership ended.");
            }
        }

        return OidcResults.SignIn(options, user with { AuthenticationBindings = CredentialLineage.Read(principal) }, authenticationTime, principal.GetScopes(),
            OidcResults.Audiences(tenant, options, request.ClientId ?? principal.GetPresenters().FirstOrDefault()), refreshExpiresAt, acting?.Record, organization,
            organizationPicker: await OidcResults.OrganizationPickerAsync(settings, tenant, request.ClientId, cancellationToken));
    }
}

public sealed class UserInfoGetEndpoint(Impersonations impersonations, TotpCredentials mfa) : UserInfoEndpoint(impersonations, mfa)
{
    public override void Configure()
    {
        Get(Path);
        Name(RouteName);
        this.Describe();
    }
}

public sealed class UserInfoPostEndpoint(Impersonations impersonations, TotpCredentials mfa) : UserInfoEndpoint(impersonations, mfa)
{
    public override void Configure()
    {
        Post(Path);
        Name(PostRouteName);
        this.AcceptsForm();
        this.Describe();
    }
}

public abstract class UserInfoEndpoint(Impersonations impersonations, TotpCredentials mfa) : EndpointWithoutRequest<IResult>
{
    internal const string RouteName = "oauth_userinfo";

    internal const string PostRouteName = "oauth_userinfo_post";

    protected const string Path = "/{slug}/oauth/userinfo";

    protected void AcceptsForm() =>
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));

    protected void Describe()
    {
        Summary("UserInfo endpoint");
        Description("GET, or POST with an application/x-www-form-urlencoded body. Requires an access token issued by this issuer and not revoked, with the user's current authentication epoch. Tokens of active MFA accounts without the ts_auth_mfa claim and identity tokens are rejected. Unavailable or corrupt MFA state answers 503 temporarily_unavailable.");
        Tags("OAuth");
        Produces<UserInfoBody>();
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<OAuthErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await this.ClaimsAsync(cancellationToken);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status503ServiceUnavailable)
        {
            return TypedResults.Json(new OAuthErrorBody(OidcErrors.TemporarilyUnavailable, error.Message), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task<IResult> ClaimsAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is null)
        {
            return TypedResults.NotFound();
        }

        var principal = (await this.HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        var tenant = Tenancy.Current(this.HttpContext);
        var acting = principal?.GetClaim(Impersonations.Claim) is { } actingId
            ? await impersonations.FindAsync(tenant, actingId, cancellationToken) : null;
        var user = principal?.GetClaim(Claims.Subject) is not { } subject ? null
            : principal.HasClaim(claim => claim.Type == Impersonations.Claim)
                ? acting?.User.Id == subject ? acting.User : null
                : await mfa.PrimaryUserAsync(tenant, subject, principal.GetClaim(OidcIssuer.EpochClaim), principal.GetClaim(OidcIssuer.StateClaim),
                    principal.GetClaim(OidcIssuer.MfaClaim), cancellationToken);
        if (user is null || principal is null)
        {
            return TypedResults.Challenge(OidcResults.Error(OidcErrors.InvalidToken, "The access token no longer identifies a user."), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        var email = principal.HasScope(Scopes.Email);
        var profile = principal.HasScope(Scopes.Profile);
        var mailbox = email && !user.IsAnonymous;
        return TypedResults.Ok(new UserInfoBody(user.Id, mailbox ? user.Email : null, mailbox ? user.EmailVerified : null,
            profile ? user.FirstName : null, profile ? user.LastName : null, profile ? user.ImageUrl : null,
            profile ? JsonElement.Parse(user.PublicMetadata) : null, acting is null ? null : new ImpersonationActor(acting.Record.ActorId),
            user.IsAnonymous ? true : null));
    }
}

public sealed class RevocationEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/{slug}/oauth/revoke");
        Name("oauth_revoke");
        Summary("Revocation endpoint");
        Description("RFC 7009, application/x-www-form-urlencoded token (and optionally token_type_hint); the client authenticates as at the token endpoint and may revoke only its own tokens: confidential clients with client_secret_basic or client_secret_post, a public client (no secret registered) by sending only client_id in the form (RFC 7009 section 2.1). Revoking a refresh or access token ends its whole sign-in: every refresh token of it is refused and ts-auth stops accepting its access tokens, while access tokens validated offline (TerraBase) stay valid until they expire, at most 5 minutes. An unknown, expired or already revoked token also answers 200.");
        Tags("OAuth");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<Dictionary<string, object>>();
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic, PublicClientAlternative: true));
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IResult>(TypedResults.NotFound());
}

public sealed class LogoutGetEndpoint(Impersonations impersonations, Sessions sessions, ProjectAuthSettings settings, AuthOptions options) : LogoutEndpoint(impersonations, sessions, settings, options)
{
    public override void Configure()
    {
        Get(Path);
        Name("oauth_logout");
        this.Describe();
    }
}

public sealed class LogoutPostEndpoint(Impersonations impersonations, Sessions sessions, ProjectAuthSettings settings, AuthOptions options) : LogoutEndpoint(impersonations, sessions, settings, options)
{
    public override void Configure()
    {
        Post(Path);
        Name("oauth_logout_post");
        this.AcceptsForm();
        this.Describe();
    }
}

public abstract class LogoutEndpoint(Impersonations impersonations, Sessions sessions, ProjectAuthSettings settings, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    protected const string Path = "/{slug}/oauth/logout";

    public const string ConfirmField = "confirm";

    private static readonly string[] Carried = [Parameters.IdTokenHint, Parameters.ClientId, Parameters.PostLogoutRedirectUri, Parameters.State, Parameters.UiLocales, "logout_hint"];

    protected void AcceptsForm() =>
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));

    protected void Describe()
    {
        Summary("End-session endpoint");
        Description("OpenID Connect RP-Initiated Logout 1.0, by GET with query parameters or POST with the same parameters as application/x-www-form-urlencoded. post_logout_redirect_uri must exactly match one registered for the client identified by client_id or id_token_hint and, for a project with a nonempty redirect allowlist, an entry in that list. A disallowed project URI answers a local error without navigation. The answer is otherwise the hosted Signed out page; with a validated post_logout_redirect_uri it continues there at once (with state, if sent), and a Continue link to the same address appears after 1.5 seconds for a browser that blocks the navigation. A live hosted session ends only when id_token_hint was issued to its user or the user confirms: otherwise the answer is a Sign out? page whose same-origin form POST (confirm=1, carrying the request's parameters) ends it, and the request itself changes nothing. Access and refresh tokens already issued stay valid until they expire or are revoked.");
        Tags("OAuth");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { } request)
        {
            return TypedResults.NotFound();
        }

        var tenant = Tenancy.Current(this.HttpContext);
        if (!await OidcResults.AllowsRedirectAsync(settings, tenant, request.PostLogoutRedirectUri, cancellationToken))
        {
            return OidcResults.DisallowedRedirect();
        }

        var cookie = this.HttpContext.Request.Cookies[Tenancy.SessionCookie(tenant)];
        var actingCookie = this.HttpContext.Request.Cookies[Impersonations.Cookie(tenant)];
        var acting = await impersonations.ResolveAsync(tenant, actingCookie, cancellationToken);
        var subject = actingCookie is not null ? acting?.User.Id : (await sessions.ResolveAsync(tenant, cookie, cancellationToken))?.User.Id;
        if (subject is not null && !await this.ConfirmedAsync(request, subject))
        {
            var fields = Carried.Select(name => (Name: name, Value: (string?)request[name]))
                .Where(field => !string.IsNullOrEmpty(field.Value))
                .Select(field => KeyValuePair.Create(field.Name, field.Value!))
                .ToList();
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignOutConfirm, tenant) { LogoutFields = fields });
        }

        if (actingCookie is not null)
        {
            if (acting is not null)
            {
                await impersonations.EndAsync(tenant, acting.Record.Id, acting.Record.ActorId, "manual", cancellationToken);
            }

            this.HttpContext.Response.Cookies.Delete(Impersonations.Cookie(tenant), ImpersonationBrowser.CookieOptions(null));
        }
        else
        {
            await sessions.RevokeAsync(tenant, cookie, cancellationToken);
            BrowserSecurity.DeleteSessionCookie(this.HttpContext.Response, tenant);
        }
        var returnTo = request.PostLogoutRedirectUri is { } uri
            ? (string.IsNullOrEmpty(request.State) ? uri : Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(uri, Parameters.State, request.State))
            : null;
        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignedOut, tenant) { ReturnTo = returnTo });
    }

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

internal static class OidcResults
{
    public static async Task<bool> OrganizationPickerAsync(ProjectAuthSettings settings, string tenant, string? clientId, CancellationToken cancellationToken) =>
        tenant == OidcIssuer.InstanceId && clientId == OidcIssuer.DashboardClientId
            && (await settings.GetAsync(PlatformBootstrap.AuthProjectId, cancellationToken)).OrganizationPicker;

    public static IResult DisallowedRedirect() => TypedResults.Json(new OAuthErrorBody(OidcErrors.InvalidRequest,
        "The project does not permit this redirect URI."), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status400BadRequest);

    public static async Task<bool> AllowsRedirectAsync(ProjectAuthSettings settings, string tenant, string? uri, CancellationToken cancellationToken)
    {
        if (!Tenancy.IsProject(tenant) || uri is null)
        {
            return true;
        }

        var allowed = (await settings.GetAsync(tenant, cancellationToken)).Configuration?.RedirectUrls;
        return allowed is not { Count: > 0 } || allowed.Contains(uri, StringComparer.Ordinal);
    }

    public static AuthenticationProperties Error(string error, string description) => new(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
    });

    public static readonly IReadOnlyList<string> SignInScopes = [Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.OfflineAccess];

    public static IResult Forbid(string error, string description) =>
        TypedResults.Forbid(Error(error, description), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    public static IReadOnlyList<string> Audiences(string tenant, AuthOptions options, string? clientId) =>
        !Tenancy.IsProject(tenant) ? OidcIssuer.SignInAudiences(options)
        : clientId is { Length: > 0 } ? [clientId]
        : throw new InvalidOperationException("A project sign-in has no client.");

    public const string PlatformRoleClaim = "platform_role";

    public const string PlatformRoleAdmin = "admin";

    public static IResult SignIn(AuthOptions options, UserRecord user, DateTimeOffset authenticatedAt, IEnumerable<string> scopes, IReadOnlyList<string> audiences,
        DateTimeOffset? refreshExpiresAt = null, ImpersonationRecord? impersonation = null, OrganizationClaims? organization = null, bool organizationPicker = false,
        string? agentId = null, TimeSpan? lifetime = null)
    {
        if (scopes.Except(SignInScopes).Any())
        {
            return Forbid(OidcErrors.InvalidScope, "A sign-in grants only openid, email, profile and offline_access.");
        }

        var dataAccess = impersonation is null && Tenancy.IsProject(user.InstanceId);
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role)
            .SetClaim(Claims.Subject, user.Id)
            .SetClaim("token_use", "user")
            .SetClaim(Claims.Email, user.IsAnonymous ? null : user.Email)
            .SetClaim(Claims.EmailVerified, user.IsAnonymous ? null : user.EmailVerified)
            .SetClaim(GuestSignIn.Claim, user.IsAnonymous ? true : null)
            .SetClaim(Claims.GivenName, user.FirstName)
            .SetClaim(Claims.FamilyName, user.LastName)
            .SetClaim(Claims.Picture, user.ImageUrl)
            .SetClaim("public_metadata", JsonElement.Parse(user.PublicMetadata))
            .SetClaim(Claims.AuthenticationTime, authenticatedAt.ToUnixTimeSeconds())
            .SetClaim(OidcIssuer.EpochClaim, user.AuthEpoch)
            .SetClaim(OidcIssuer.StateClaim, user.AuthStamp)
            .SetClaim("ts_auth_session", user.SessionId)
            .SetClaim(OidcIssuer.MfaClaim, user.MultiFactor ? user.MfaMethod ?? "totp" : null)
            .SetClaim(FixedRefreshTokens.Claim, refreshExpiresAt?.ToUnixTimeSeconds())
            .SetClaim(OidcIssuer.OrganizationPickerClaim, organizationPicker ? "true" : null)
            .SetScopes(scopes)
            .SetResources(dataAccess ? [.. audiences, .. options.ProjectDataAudiences(user.InstanceId)] : audiences);
        if (dataAccess)
        {
            identity.SetClaim(EndUserTokens.KindClaim, EndUserTokens.Kind).SetClaim(EndUserTokens.ProjectClaim, user.InstanceId);
        }

        if (dataAccess && user.CustomClaims is { } trusted)
        {
            identity.SetClaim(TrustedClaims.Claim, JsonElement.Parse(trusted));
        }

        if (agentId is null && user.InstanceId == OidcIssuer.InstanceId && (user.PlatformRole == PlatformRoleAdmin || user.Email == PlatformBootstrap.OwnerEmail))
        {
            identity.SetClaim(PlatformRoleClaim, PlatformRoleAdmin);
        }

        if (impersonation is not null)
        {
            identity.SetClaim(Impersonations.Claim, impersonation.Id);
            identity.SetClaim("act", JsonSerializer.SerializeToElement(new ImpersonationActor(impersonation.ActorId), ImpersonationJson.Default.ImpersonationActor));
        }

        if (agentId is not null)
        {
            identity.SetClaim(LiveAgents.Claim, agentId);
            identity.SetClaim("act", JsonSerializer.SerializeToElement(new ImpersonationActor(agentId), ImpersonationJson.Default.ImpersonationActor));
        }

        var profile = identity.HasScope(Scopes.Profile);
        if (organization is not null)
        {
            identity.SetClaim("org_id", organization.Id).SetClaim("org_slug", organization.Slug).SetClaim("org_role", organization.Role);
        }

        var email = identity.HasScope(Scopes.Email);
        identity.SetDestinations(claim => claim.Type switch
        {
            Claims.Subject or "act" or Impersonations.Claim => [Destinations.AccessToken, Destinations.IdentityToken],
            Claims.GivenName or Claims.FamilyName or Claims.Picture or "public_metadata" => profile ? [Destinations.IdentityToken] : [],
            Claims.Email or Claims.EmailVerified => email ? [Destinations.AccessToken, Destinations.IdentityToken] : [Destinations.AccessToken],
            Claims.AuthenticationTime or "org_id" or "org_slug" or "org_role" or GuestSignIn.Claim => [Destinations.AccessToken, Destinations.IdentityToken],
            "ts_auth_session" or "token_use" or LiveAgents.Claim or TrustedClaims.Claim or OidcIssuer.EpochClaim or OidcIssuer.StateClaim or OidcIssuer.MfaClaim or PlatformRoleClaim
                or EndUserTokens.KindClaim or EndUserTokens.ProjectClaim => [Destinations.AccessToken],
            OidcIssuer.OrganizationPickerClaim => [Destinations.IdentityToken],
            _ => [],
        });
        var principal = new ClaimsPrincipal(identity);
        if (lifetime is { } limit)
        {
            principal.SetAccessTokenLifetime(limit);
        }

        CredentialLineage.Attach(principal, user.AuthenticationBindings);
        return TypedResults.SignIn(principal, null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
