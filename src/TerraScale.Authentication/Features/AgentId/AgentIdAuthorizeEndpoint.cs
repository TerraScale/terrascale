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
    public const string Continue = "ts_agentid_continue";

    protected void Describe(string operationId)
    {
        Name(operationId);
        Summary("Sign in as agent: authorization endpoint");
        Description("The authorization endpoint of the agent issuer {origin}/agents, by GET with query parameters or POST with the same parameters form-encoded. Authorization code flow only (response_type=code) with PKCE code_challenge_method=S256; scopes openid and profile. client_id is an https origin the app controls (http only on a loopback host), and redirect_uri must be on that origin. Without a proof for this exact request it answers a one-time transaction that lives 5 minutes: an HTML page that reloads itself every 2 seconds, or with Accept: application/json a JSON body with status pending, transaction_id, proof_url, client_id, client_name and expires_at. Both carry Link: <proof_url>; rel=\"agent-proof\". Once an agent posted a valid proof to proof_url, the same request (any parameter order) redirects to redirect_uri with code and state, once; the browser page first shows that it signed in. An expired or used transaction answers 400 expired_transaction. Errors after client and redirect validation go to the client. Per caller address and per client_id it allows 120 and 600 requests a minute; more answer 429 rate_limited with Retry-After.");
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

        var parameters = HttpMethods.IsPost(this.HttpContext.Request.Method)
            ? (await this.HttpContext.Request.ReadFormAsync(cancellationToken)).ToList()
            : this.HttpContext.Request.Query.ToList();
        var resumed = parameters.Any(parameter => parameter.Key == Continue);
        List<KeyValuePair<string, StringValues>> own = [.. parameters.Where(parameter => parameter.Key != Continue)];
        var browser = this.HttpContext.Request.GetTypedHeaders().Accept.Any(media => media.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase));
        try
        {
            await transactions.LimitAsync("agentid-authorize", SignInFlow.Ip(this.HttpContext), clientId, cancellationToken);
        }
        catch (ApiException error) when (error.RetryAfter is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        var id = AgentIdTransactions.Id(own);
        var transaction = await transactions.OpenAsync(id, clientId, cancellationToken);
        var proofUrl = AgentIdIssuer.ProofUrl(options, id);
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!transactions.IsOpen(transaction) || transaction.ClientId != clientId)
        {
            return browser
                ? AgentIdPageModel.Render(this.HttpContext, new(AgentIdStep.Expired, clientId), StatusCodes.Status400BadRequest)
                : Results.Json(new OAuthErrorBody("expired_transaction", AgentIdTransactions.Expired), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status400BadRequest);
        }

        var self = AgentIdIssuer.AuthorizePath + QueryString.Create(own);
        if (transaction.ProvedAt is null)
        {
            this.HttpContext.Response.Headers.Link = $"<{proofUrl}>; rel=\"agent-proof\"";
            if (browser)
            {
                this.HttpContext.Response.Headers["Refresh"] = "2;url=" + self;
                return AgentIdPageModel.Render(this.HttpContext, new(AgentIdStep.Waiting, clientId, proofUrl));
            }

            return Results.Json(new AgentIdPendingBody("pending", id, proofUrl, clientId, clientId, transaction.ExpiresAt), AgentIdJson.Default.AgentIdPendingBody);
        }

        if (browser && !resumed)
        {
            this.HttpContext.Response.Headers["Refresh"] = "1;url=" + self + "&" + Continue + "=1";
            return AgentIdPageModel.Render(this.HttpContext, new(AgentIdStep.Done, clientId));
        }

        if (await transactions.CompleteAsync(transaction, cancellationToken) is not { } signIn)
        {
            return Results.Forbid(OidcResults.Error(Errors.AccessDenied, "The agent can no longer sign in."), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        return Results.SignIn(AgentIdIssuer.Principal(options, clientId, signIn.Agent, signIn.Owner.Id, request.GetScopes()), null,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
