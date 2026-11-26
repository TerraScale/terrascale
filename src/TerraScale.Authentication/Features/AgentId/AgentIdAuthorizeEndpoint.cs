using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using OpenIddict.Abstractions;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.AgentId;

public abstract class AgentIdAuthorizeEndpoint(AgentIdTransactions transactions, IOpenIddictApplicationManager applications, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    protected void Describe(string operationId)
    {
        Name(operationId);
        Summary("Sign in as agent: authorization endpoint");
        Description("The authorization endpoint of the agent issuer {origin}/agents, by GET with query parameters or POST with the same parameters form-encoded. Authorization code flow only (response_type=code) with PKCE code_challenge_method=S256; scopes openid, profile and, for a registered client only, owner_email. client_id is either the canonical https origin the app controls (lowercase ASCII host, punycode for international names, no default port, path, trailing slash or userinfo), with redirect_uri an https URL on that same origin, or the client_id of a registered client (POST /v1/terrascale/agentid-clients), with redirect_uri exactly one of its redirect_uris. An origin client asking for owner_email is answered invalid_scope at the redirect_uri. Without a proof for this exact request it answers a one-time transaction that lives 5 minutes: an HTML page that reloads itself every 2 seconds, or with Accept: application/json a JSON body with status pending, transaction_id, proof_url, client_id, client_name (the origin, or a registered client's name) and expires_at. Both carry Link: <proof_url>; rel=\"agent-proof\". Once an agent posted a valid proof to proof_url, the same request (any parameter order) redirects to redirect_uri with code, state and iss, once. A request without the openid scope is answered invalid_scope at the redirect_uri before any transaction opens. An expired or used transaction answers 400 expired_transaction. Errors after client and redirect validation go to the client. Per caller address and per client_id it allows 120 and 600 requests a minute; more answer 429 rate_limited with Retry-After.");
        Tags("Sign in as agent");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<AgentIdPendingBody>(StatusCodes.Status200OK);
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<OAuthErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    protected void AcceptsForm() => Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { ClientId: { } clientId } request)
        {
            return TypedResults.NotFound();
        }

        if (!request.HasScope(Scopes.OpenId))
        {
            return TypedResults.Forbid(OidcResults.Error(OidcErrors.InvalidScope, "Sign in as agent requires the openid scope."), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        if (request.HasScope(AgentIdIssuer.OwnerEmailScope) && AgentIdIssuer.IsOpenClient(clientId))
        {
            return TypedResults.Forbid(OidcResults.Error(OidcErrors.InvalidScope, "Only a registered client can request the owner_email scope."), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        List<KeyValuePair<string, StringValues>> parameters = HttpMethods.IsPost(this.HttpContext.Request.Method)
            ? [.. await this.HttpContext.Request.ReadFormAsync(cancellationToken)]
            : [.. this.HttpContext.Request.Query];
        try
        {
            if (await transactions.LimitAsync("agentid-authorize", SignInFlow.Ip(this.HttpContext), clientId, cancellationToken) is { } limited)
            {
                return HostedAuth.TooMany(limited);
            }
        }
        catch (ApiException unavailable) when (unavailable.RetryAfter is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        var browser = this.HttpContext.Request.GetTypedHeaders().Accept.Any(media => media.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase));

        var transaction = await transactions.OpenAsync(AgentIdTransactions.RequestHash(parameters), clientId, cancellationToken);
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        var clientName = await applications.FindByClientIdAsync(clientId, cancellationToken) is OidcApplication { DisplayName: { } name }
            ? name
            : throw new InvalidOperationException("OpenIddict passed an authorization request of an unknown client.");
        if (!transactions.IsOpen(transaction) || transaction.ClientId != clientId)
        {
            return browser
                ? AgentIdPageModel.Render(this.HttpContext, new(AgentIdStep.Expired, clientName), StatusCodes.Status400BadRequest)
                : TypedResults.Json(new OAuthErrorBody("expired_transaction", AgentIdTransactions.Expired), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status400BadRequest);
        }

        if (transaction.ProvedAt is null)
        {
            var proofUrl = AgentIdIssuer.ProofUrl(options, transaction.Id);
            this.HttpContext.Response.Headers.Link = $"<{proofUrl}>; rel=\"agent-proof\"";
            if (browser)
            {
                this.HttpContext.Response.Headers["Refresh"] = "2;url=" + AgentIdIssuer.AuthorizePath + QueryString.Create(parameters);
                return AgentIdPageModel.Render(this.HttpContext, new(AgentIdStep.Waiting, clientName, proofUrl));
            }

            return TypedResults.Json(new AgentIdPendingBody("pending", transaction.Id, proofUrl, clientId, clientName, transaction.ExpiresAt), AgentIdJson.Default.AgentIdPendingBody);
        }

        return await transactions.CompleteAsync(transaction, cancellationToken) is { } signIn
            ? TypedResults.SignIn(AgentIdIssuer.Principal(options, clientId, signIn, request.GetScopes(), redemption: false), null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
            : TypedResults.Forbid(OidcResults.Error(OidcErrors.AccessDenied, "The agent can no longer sign in."), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }
}
