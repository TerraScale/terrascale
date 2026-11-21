using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class CreateAgentIdClientEndpoint(PlatformAuthentication authentication, AgentIdClients clients, StepUpProofs proofs) : AgentIdClientEndpoint(authentication)
{
    public override void Configure()
    {
        Post(Root);
        this.Describe("create_agentid_client", "Register a Sign in as agent client",
            "Registers a confidential client of the agent issuer {origin}/agents for the signed-in TerraScale account. name is 1 to 64 characters; redirect_uris holds 1 to 10 distinct absolute https URLs in canonical form (lowercase scheme and host, no default port, userinfo or fragment), matched exactly at authorization. The answer has client_id (a ULID) and client_secret, shown once; only its SHA-256 digest is kept. The client authenticates at {origin}/agents/oauth/token with client_secret_basic or client_secret_post and may request the owner_email scope. An account has at most 10 clients (409 client_limit). Needs a create_oauth_client step-up proof in X-TerraScale-Step-Up-Proof, spent in the same save as the change, so a sign-out everywhere that commits first refuses it with 409; a token an agent acts with is 403 owner_required.",
            "Sign in as agent", typeof(CreateAgentIdClientRequest), StatusCodes.Status201Created, typeof(AgentIdClientBody), conflict: true, scoped: false);
    }

    protected override async Task<IResult> HandleOwnerAsync(UserRecord owner, CancellationToken cancellationToken)
    {
        var request = await this.BodyAsync(AgentIdClientJson.Default.CreateAgentIdClientRequest, cancellationToken);
        var name = PlatformDirectory.Name(request.Name) is { Length: <= 64 } named ? named : throw Invalid("name must be 1 to 64 characters with a letter or digit.");
        List<string> redirects = [.. (request.RedirectUris ?? []).Select(value => AgentIdClients.RedirectUri(value)
            ?? throw Invalid("Each redirect URI must be an absolute https URL in canonical form, without userinfo or fragment."))];
        if (redirects.Count is < 1 or > AgentIdClients.MaxRedirectUris || redirects.Distinct(StringComparer.Ordinal).Count() != redirects.Count)
        {
            throw Invalid("redirect_uris must hold 1 to 10 distinct URLs.");
        }

        var proof = await proofs.PrepareHeaderAsync(owner, this.HttpContext.Request, "create_oauth_client", null, cancellationToken) ?? throw StepUpProofs.Required();
        return await clients.CreateAsync(proof, name, redirects, cancellationToken) switch
        {
            AgentIdClientBody client => Results.Json(client, AgentIdClientJson.Default.AgentIdClientBody, statusCode: StatusCodes.Status201Created)
                .WithLocation(Root + "/" + client.ClientId),
            NotFound notFound => ErrorResults.ToResult(notFound),
            Conflict conflict => ErrorResults.ToResult(conflict),
            Forbidden forbidden => ErrorResults.ToResult(forbidden),
        };
    }
}
