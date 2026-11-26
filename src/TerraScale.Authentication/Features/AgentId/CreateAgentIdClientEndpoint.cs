using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class CreateAgentIdClientEndpoint(PlatformAuthentication authentication, AgentIdClients clients, StepUpProofs proofs) : AgentIdClientEndpoint(authentication)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Post(Root);
        this.Describe("create_agentid_client", "Register a Sign in as agent client",
            "Registers a confidential client of the agent issuer {origin}/agents for the signed-in TerraScale account. name is 1 to 64 characters; redirect_uris holds 1 to 10 distinct absolute https URLs in canonical form (lowercase scheme and host, no default port, userinfo or fragment), matched exactly at authorization. The answer has client_id (a ULID) and client_secret, shown once; only its SHA-256 digest is kept. The client authenticates at {origin}/agents/oauth/token with client_secret_basic or client_secret_post and may request the owner_email scope. An account has at most 10 clients (409 client_limit). Needs a create_oauth_client step-up proof in X-TerraScale-Step-Up-Proof, spent in the same save as the change, so a sign-out everywhere that commits first refuses it with 409; a token an agent acts with is 403 owner_required.",
            "Sign in as agent", typeof(CreateAgentIdClientRequest), StatusCodes.Status201Created, typeof(AgentIdClientBody), conflict: true, scoped: false);
    }

    protected override async Task<IResult> HandleOwnerAsync(UserRecord owner, CancellationToken cancellationToken)
    {
        var request = await this.BodyAsync(AgentIdClientJson.Default.CreateAgentIdClientRequest, cancellationToken);
        var name = PlatformDirectory.Name(request.Name);
        if (name is not { Length: <= 64 })
        {
            return ErrorResults.ToResult(new AuthValidation("name must be 1 to 64 characters with a letter or digit."));
        }

        List<string> redirects = [];
        foreach (var value in request.RedirectUris ?? [])
        {
            if (AgentIdClients.RedirectUri(value) is not { } redirect)
            {
                return ErrorResults.ToResult(new AuthValidation("Each redirect URI must be an absolute https URL in canonical form, without userinfo or fragment."));
            }

            redirects.Add(redirect);
        }

        if (redirects.Count is < 1 or > AgentIdClients.MaxRedirectUris || redirects.Distinct(StringComparer.Ordinal).Count() != redirects.Count)
        {
            return ErrorResults.ToResult(new AuthValidation("redirect_uris must hold 1 to 10 distinct URLs."));
        }

        var proof = await proofs.PrepareHeaderAsync(owner, this.HttpContext.Request, "create_oauth_client", null, cancellationToken);
        if (proof is null)
        {
            return ErrorResults.ToResult(new AuthForbidden("A fresh, approved, purpose-bound single-use proof is required.", "step_up_required"));
        }

        return await clients.CreateAsync(proof, name, redirects, cancellationToken) switch
        {
            AgentIdClientBody client => TypedResults.Json(client, AgentIdClientJson.Default.AgentIdClientBody, statusCode: StatusCodes.Status201Created)
                .WithLocation(Root + "/" + client.ClientId),
            AuthNotFound notFound => ErrorResults.ToResult(notFound),
            AuthConflict conflict => ErrorResults.ToResult(conflict),
            AuthForbidden forbidden => ErrorResults.ToResult(forbidden),
        };
    }
}
