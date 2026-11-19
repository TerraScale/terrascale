using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.AgentId;

public abstract class AgentIdAuthorizeEndpoint(AgentIdTransactions transactions, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    protected void Describe(string operationId)
    {
        Name(operationId);
        Summary("Sign in as agent: authorization endpoint");
        Description("The authorization endpoint of the agent issuer {origin}/agents, by GET with query parameters or POST with the same parameters form-encoded. Authorization code flow only (response_type=code) with PKCE code_challenge_method=S256; scopes openid and profile. client_id is the canonical https origin the app controls (lowercase ASCII host, punycode for international names, no default port, path, trailing slash or userinfo), and redirect_uri must be an https URL on that same origin. Without a proof for this exact request it answers a one-time transaction that lives 5 minutes: an HTML page that reloads itself every 2 seconds, or with Accept: application/json a JSON body with status pending, transaction_id, proof_url, client_id, client_name and expires_at. Both carry Link: <proof_url>; rel=\"agent-proof\". Once an agent posted a valid proof to proof_url, the same request (any parameter order) redirects to redirect_uri with code, state and iss, once. A request without the openid scope is answered invalid_scope at the redirect_uri before any transaction opens. An expired or used transaction answers 400 expired_transaction. Errors after client and redirect validation go to the client. Per caller address and per client_id it allows 120 and 600 requests a minute; more answer 429 rate_limited with Retry-After.");
        Tags("Sign in as agent");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<AgentIdPendingBody>(StatusCodes.Status200OK);
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
        AllowAnonymous();
    }

    protected void AcceptsForm() => Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { ClientId: { } clientId } request)
        {
            return Results.NotFound();
        }

        if (!request.HasScope(Scopes.OpenId))
        {
            return Results.Forbid(OidcResults.Error(Errors.InvalidScope, "Sign in as agent requires the openid scope."), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        List<KeyValuePair<string, StringValues>> parameters = HttpMethods.IsPost(this.HttpContext.Request.Method)
            ? [.. await this.HttpContext.Request.ReadFormAsync(cancellationToken)]
            : [.. this.HttpContext.Request.Query];
        var browser = this.HttpContext.Request.GetTypedHeaders().Accept.Any(media => media.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase));
        try
        {
            await transactions.LimitAsync("agentid-authorize", SignInFlow.Ip(this.HttpContext), clientId, cancellationToken);
        }
        catch (ApiException error) when (error.RetryAfter is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        var transaction = await transactions.OpenAsync(AgentIdTransactions.RequestHash(parameters), clientId, cancellationToken);
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!transactions.IsOpen(transaction) || transaction.ClientId != clientId)
        {
            return browser
                ? AgentIdPageModel.Render(this.HttpContext, new(AgentIdStep.Expired, clientId), StatusCodes.Status400BadRequest)
                : Results.Json(new OAuthErrorBody("expired_transaction", AgentIdTransactions.Expired), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status400BadRequest);
        }

        if (transaction.ProvedAt is null)
        {
            var proofUrl = AgentIdIssuer.ProofUrl(options, transaction.Id);
            this.HttpContext.Response.Headers.Link = $"<{proofUrl}>; rel=\"agent-proof\"";
            if (browser)
            {
                this.HttpContext.Response.Headers["Refresh"] = "2;url=" + AgentIdIssuer.AuthorizePath + QueryString.Create(parameters);
                return AgentIdPageModel.Render(this.HttpContext, new(AgentIdStep.Waiting, clientId, proofUrl));
            }

            return Results.Json(new AgentIdPendingBody("pending", transaction.Id, proofUrl, clientId, clientId, transaction.ExpiresAt), AgentIdJson.Default.AgentIdPendingBody);
        }

        return await transactions.CompleteAsync(transaction, cancellationToken) is { } signIn
            ? Results.SignIn(AgentIdIssuer.Principal(options, clientId, signIn, request.GetScopes()), null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
            : Results.Forbid(OidcResults.Error(Errors.AccessDenied, "The agent can no longer sign in."), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }
}
