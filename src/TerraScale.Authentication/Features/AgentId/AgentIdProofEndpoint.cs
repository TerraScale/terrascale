using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class AgentIdProofEndpoint(PlatformAuthentication authentication, AgentIdTransactions transactions) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Post(AgentIdIssuer.ProofRoute);
        this.Describe("agentid_prove", "Sign in as agent: prove the agent",
            "Unauthenticated; the agent answers the transaction its authorization request opened. proof is an ES256 JWT signed by an active key of an active agent of a TerraScale account: header kid the key's kid and no jwk, jku, x5u, x5c or crit; iss and sub the agent id; aud exactly this URL; txn the transaction id; client_id the client_id of the authorization request; a jti used once per agent; integer iat and exp with exp at most 300 seconds after iat (30 seconds of clock skew). A valid proof answers 204 and the same authorization request then completes once. Every other proof (bad signature, header, claims or times, a replayed jti, a revoked or expired agent or key, an owner disabled or signed out everywhere since the agent was created, a transaction already proved) answers the same 401 invalid_proof and reveals nothing. An unknown, expired or used transaction answers 404 expired_transaction. Per agent 30 and per client_id 600 proofs a minute; more answer 429 rate_limited with Retry-After.",
            "Sign in as agent", typeof(AgentIdProofRequest), StatusCodes.Status204NoContent, null, roleChecked: false);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        AllowAnonymous();
    }

    protected override async Task<IResult> AuthenticateAndHandleAsync(CancellationToken cancellationToken)
    {
        var transaction = this.HttpContext.GetRouteValue("transaction") as string ?? string.Empty;
        var request = await this.BodyAsync(AgentIdJson.Default.AgentIdProofRequest, cancellationToken);
        return await transactions.ProveAsync(transaction, request.Proof, cancellationToken) switch
        {
            AuthCompleted => TypedResults.NoContent(),
            AuthNotFound notFound => ErrorResults.ToResult(notFound),
            AuthUnauthorized unauthorized => ErrorResults.ToResult(unauthorized),
        };
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The agent proof endpoint is anonymous.");
}
