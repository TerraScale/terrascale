using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Metadata;
using OpenIddict.Abstractions;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Storage;
using TerraScale.Endpoints;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class AgentIdTokenEndpoint(AgentIdTransactions transactions, AttemptLimiter limiter, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public const int TokensPerAgentMinute = 60;

    public override void Configure()
    {
        Post(AgentIdIssuer.TokenPath);
        Name("agentid_token");
        Summary("Sign in as agent: token endpoint");
        Description("application/x-www-form-urlencoded grant_type=authorization_code with code, redirect_uri and code_verifier. An origin client is public: it sends client_id and no secret. A registered client (POST /v1/terrascale/agentid-clients) authenticates with client_secret_basic (HTTP Basic client_id:client_secret) or client_secret_post (client_id and client_secret in the form); a missing, wrong, retired or deleted client's secret answers 401 invalid_client. The registration, its owner and the secret it was checked against are asserted in the same save that consumes the code, so a rotation without grace or a deletion that commits first fails the redemption with invalid_grant and leaves the code unconsumed. The code lives 60 seconds and is redeemed once; a second redemption answers invalid_grant. The agent, the key that proved the sign-in and the owner are checked again, and their row versions are asserted in the same save that consumes the code, so a revocation that commits first fails the redemption with invalid_grant: a revoked or expired agent, a revoked proof key, or an owner disabled or signed out everywhere answers invalid_grant. The answer has id_token (ES256, signed by the agent issuer's keys in {origin}/agents/.well-known/jwks.json, aud the client_id; for a registered client granted owner_email it carries owner_email and owner_email_verified only while the agent's owner shares their email for this agent), an encrypted access_token for the userinfo endpoint only, token_type Bearer, expires_in 300 and scope; never a refresh token. Every POST charges the caller address's shared token budget (as /terrascale/oauth/token), and each client_id 600 and each agent 60 redemptions a minute; more answer 429 rate_limited with Retry-After. Storage that does not answer answers 503 temporarily_unavailable.");
        Tags("Sign in as agent");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(TokenRequestForm)));
        Produces<TokenBody>();
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<OAuthErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        this.HttpContext.Response.Headers.Pragma = "no-cache";
        try
        {
            return await this.GrantAsync(cancellationToken);
        }
        catch (Exception error) when ((AuthData.IsUnavailable(error) || AuthData.IsContention(error)) && !this.HttpContext.RequestAborted.IsCancellationRequested)
        {
            var unavailable = AuthData.Unavailable(error);
            ErrorEnvelopeMiddleware.ApplyRetryAfter(this.HttpContext.Response, unavailable.RetryAfter);
            return TypedResults.Json(new OAuthErrorBody(OidcErrors.TemporarilyUnavailable, unavailable.Message), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task<IResult> GrantAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { ClientId: { } clientId } request)
        {
            return TypedResults.NotFound();
        }

        if (!request.IsAuthorizationCodeGrantType())
        {
            return OidcResults.Forbid(OidcErrors.UnsupportedGrantType, "The agent issuer grants authorization_code only.");
        }

        var principal = (await this.HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal
            ?? throw new InvalidOperationException("OpenIddict passed a token request without a principal.");
        if (principal.GetClaim(AgentIdIssuer.AgentClaim) is not { } agentId || principal.GetClaim(AgentIdIssuer.OwnerClaim) is not { } ownerId
            || principal.GetClaim(AgentIdIssuer.KeyClaim) is not { } kid)
        {
            return OidcResults.Forbid(OidcErrors.InvalidGrant, "The code does not name an agent.");
        }

        if ((await limiter.AcquireAsync(AgentIdIssuer.Slug, "agentid-token-agent", agentId, TokensPerAgentMinute, TimeSpan.FromMinutes(1), cancellationToken)
            ?? await limiter.AcquireAsync(AgentIdIssuer.Slug, "agentid-token-client", limiter.Fold(clientId), AgentIdTransactions.RequestsPerClientMinute, TimeSpan.FromMinutes(1), cancellationToken))
            is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        if (await transactions.LiveAsync(agentId, ownerId, kid, cancellationToken) is not { } live)
        {
            return OidcResults.Forbid(OidcErrors.InvalidGrant, "The agent can no longer sign in.");
        }

        var registration = this.HttpContext.Items[new AgentIdRegistration.RequestCopy(clientId)] as AgentIdRegistration;
        if (!AgentIdIssuer.IsOpenClient(clientId) && registration is null)
        {
            return OidcResults.Forbid(OidcErrors.InvalidGrant, "The client is no longer registered.");
        }

        this.HttpContext.Items[AgentIdFence.Item] = live with { Registration = registration };
        return TypedResults.SignIn(AgentIdIssuer.Principal(options, clientId, live.SignIn, principal.GetScopes(), redemption: true), null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
