using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Metadata;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
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
        Description("application/x-www-form-urlencoded grant_type=authorization_code with code, redirect_uri, code_verifier and client_id; the client is public and sends no secret. The code lives 60 seconds and is redeemed once; a second redemption answers invalid_grant. The agent, the key that proved the sign-in and the owner are checked again, and their versions are asserted in a save before the tokens are issued: a revoked or expired agent, a revoked proof key, or an owner disabled or signed out everywhere answers invalid_grant. The answer has id_token (ES256, signed by the agent issuer's keys in {origin}/agents/.well-known/jwks.json, aud the client_id), an encrypted access_token for the userinfo endpoint only, token_type Bearer, expires_in 300 and scope; never a refresh token. Every POST charges the caller address's shared token budget (as /terrascale/oauth/token), and each client_id 600 and each agent 60 redemptions a minute; more answer 429 rate_limited with Retry-After. Storage that does not answer answers 503 temporarily_unavailable.");
        Tags("Sign in as agent");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(TokenRequestForm)));
        Produces<TokenBody>();
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
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
        catch (Exception error) when (AuthData.IsUnavailable(error) && !this.HttpContext.RequestAborted.IsCancellationRequested)
        {
            var unavailable = error as ApiException ?? ApiException.Unavailable(error);
            return Results.Json(new OAuthErrorBody(Errors.TemporarilyUnavailable, unavailable.Message), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task<IResult> GrantAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { ClientId: { } clientId } request)
        {
            return Results.NotFound();
        }

        if (!request.IsAuthorizationCodeGrantType())
        {
            return OidcResults.Forbid(Errors.UnsupportedGrantType, "The agent issuer grants authorization_code only.");
        }

        var principal = (await this.HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal
            ?? throw new InvalidOperationException("OpenIddict passed a token request without a principal.");
        if (principal.GetClaim(AgentIdIssuer.AgentClaim) is not { } agentId || principal.GetClaim(AgentIdIssuer.OwnerClaim) is not { } ownerId
            || principal.GetClaim(AgentIdIssuer.KeyClaim) is not { } kid)
        {
            return OidcResults.Forbid(Errors.InvalidGrant, "The code does not name an agent.");
        }

        if ((await limiter.AcquireAsync(AgentIdIssuer.Slug, "agentid-token-agent", agentId, TokensPerAgentMinute, TimeSpan.FromMinutes(1), cancellationToken)
            ?? await limiter.AcquireAsync(AgentIdIssuer.Slug, "agentid-token-client", limiter.Fold(clientId), AgentIdTransactions.RequestsPerClientMinute, TimeSpan.FromMinutes(1), cancellationToken))
            is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        return await transactions.LiveAsync(agentId, ownerId, kid, fence: true, cancellationToken) is { } live
            ? Results.SignIn(AgentIdIssuer.Principal(options, clientId, live, principal.GetScopes()), null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
            : OidcResults.Forbid(Errors.InvalidGrant, "The agent can no longer sign in.");
    }
}
