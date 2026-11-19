using System.Text.Json.Serialization;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Metadata;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Endpoints;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.AgentId;

public sealed record AgentIdUserInfoBody(
    [property: JsonPropertyName("sub")] string Subject,
    string ActorType,
    string? Name,
    string? OwnerSub,
    string? OwnerEmail = null,
    bool? OwnerEmailVerified = null);

public abstract class AgentIdUserInfoEndpoint(AgentIdTransactions transactions) : EndpointWithoutRequest<IResult>
{
    protected void Describe(string operationId)
    {
        Name(operationId);
        Summary("Sign in as agent: userinfo endpoint");
        Description("GET, or POST form-encoded, with an access token of the agent issuer. Answers sub (pairwise: stable for one agent and client_id, unrelated across clients) and actor_type agent, with the profile scope name (the agent's name) and owner_sub (the owner's pairwise subject for this client_id), and for a registered client granted the owner_email scope owner_email and owner_email_verified, only while the owner shares their email for this agent (share_owner_email, read on every call). Claims not granted or not shared are omitted. The agent, the key that proved the sign-in and the owner are read again on every call: a revoked or expired agent, a revoked proof key, or an owner disabled or signed out everywhere answers 401 invalid_token, as does a token of any other issuer.");
        Tags("Sign in as agent");
        Produces<AgentIdUserInfoBody>();
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        AllowAnonymous();
    }

    protected void AcceptsForm() => Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is null)
        {
            return Results.NotFound();
        }

        var principal = (await this.HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        if (principal?.GetClaim(AgentIdIssuer.AgentClaim) is not { } agentId || principal.GetClaim(AgentIdIssuer.OwnerClaim) is not { } ownerId
            || principal.GetClaim(AgentIdIssuer.KeyClaim) is not { } kid || principal.GetClaim(Claims.Subject) is not { } subject
            || await transactions.LiveAsync(agentId, ownerId, kid, cancellationToken) is not { } live)
        {
            return Results.Challenge(OidcResults.Error(Errors.InvalidToken, "The access token no longer identifies an agent that can sign in."),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        var profile = principal.HasScope(Scopes.Profile);
        var email = principal.GetPresenters() is [var clientId] && AgentIdIssuer.SharesOwnerEmail(clientId, principal.GetScopes(), live.Agent.Value);
        return Results.Json(new AgentIdUserInfoBody(subject, "agent", profile ? live.Agent.Value.Name : null, profile ? principal.GetClaim(AgentIdIssuer.OwnerSubjectClaim) : null,
            email ? live.Owner.Value.Email : null, email ? live.Owner.Value.EmailVerified : null), AgentIdJson.Default.AgentIdUserInfoBody);
    }
}
