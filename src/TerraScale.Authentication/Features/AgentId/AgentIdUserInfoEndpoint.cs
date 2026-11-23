using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Metadata;
using OpenIddict.Abstractions;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
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

public abstract class AgentIdUserInfoEndpoint(AgentIdTransactions transactions, AgentIdClients clients) : EndpointWithoutRequest<IResult>
{
    public const string RouteName = "agentid_userinfo";

    public const string PostRouteName = "agentid_userinfo_post";

    protected void Describe(string operationId)
    {
        Name(operationId);
        Summary("Sign in as agent: userinfo endpoint");
        Description("GET, or POST form-encoded, with an access token of the agent issuer. Answers sub (pairwise: stable for one agent and client_id, unrelated across clients) and actor_type agent, with the profile scope name (the agent's name) and owner_sub (the owner's pairwise subject for this client_id), and for a registered client granted the owner_email scope owner_email and owner_email_verified, only while the owner shares their email for this agent (share_owner_email, read on every call in one query with the client's registration). Claims not granted or not shared are omitted. The agent, the key that proved the sign-in and the owner are read again on every call: a revoked or expired agent, a revoked proof key, or an owner disabled or signed out everywhere answers 401 invalid_token, as does a token of a registered client that was deleted or whose account was disabled, and a token of any other issuer. Answers carry Cache-Control: no-store.");
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
            return TypedResults.NotFound();
        }

        var principal = (await this.HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        if (principal?.GetClaim(AgentIdIssuer.AgentClaim) is not { } agentId || principal.GetClaim(AgentIdIssuer.OwnerClaim) is not { } ownerId
            || principal.GetClaim(AgentIdIssuer.KeyClaim) is not { } kid || principal.GetClaim(Claims.Subject) is not { } subject
            || principal.GetPresenters() is not [var clientId]
            || await transactions.LiveAsync(agentId, ownerId, kid, cancellationToken) is not { } live)
        {
            return Refused();
        }

        var open = AgentIdIssuer.IsOpenClient(clientId);
        var registration = open ? null : await clients.OwnerEmailAsync(clientId, agentId, ownerId, cancellationToken);
        if (!open && registration is null)
        {
            return Refused();
        }

        var profile = principal.HasScope(Scopes.Profile);
        var shared = registration is not null && AgentIdIssuer.SharesOwnerEmail(clientId, principal.GetScopes(), registration.Shared) ? registration : null;
        return TypedResults.Json(new AgentIdUserInfoBody(subject, "agent", profile ? live.Agent.Value.Name : null, profile ? principal.GetClaim(AgentIdIssuer.OwnerSubjectClaim) : null,
            shared?.Email, shared?.EmailVerified), AgentIdJson.Default.AgentIdUserInfoBody);
    }

    private static ChallengeHttpResult Refused() =>
        TypedResults.Challenge(OidcResults.Error(OidcErrors.InvalidToken, "The access token no longer identifies an agent that can sign in."),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
