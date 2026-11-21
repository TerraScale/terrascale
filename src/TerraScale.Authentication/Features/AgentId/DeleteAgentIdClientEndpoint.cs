using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class DeleteAgentIdClientEndpoint(PlatformAuthentication authentication, AgentIdClients clients, StepUpProofs proofs) : AgentIdClientEndpoint(authentication)
{
    public override void Configure()
    {
        Delete(Root + "/{client_id}");
        this.Describe("delete_agentid_client", "Delete a Sign in as agent client",
            "Deletes a client the signed-in account owns: its next authorization request, token redemption and secret are refused at once. Another account's client is 404. Needs a delete_oauth_client step-up proof in X-TerraScale-Step-Up-Proof, spent in the same save as the change, so a sign-out everywhere that commits first refuses it with 409; a token an agent acts with is 403 owner_required.",
            "Sign in as agent", null, StatusCodes.Status204NoContent, null, conflict: true);
    }

    protected override async Task<IResult> HandleOwnerAsync(UserRecord owner, CancellationToken cancellationToken)
    {
        var clientId = this.RouteId("client_id");
        var proof = await proofs.PrepareHeaderAsync(owner, this.HttpContext.Request, "delete_oauth_client", null, cancellationToken);
        if (proof is null)
        {
            return ErrorResults.ToResult(new Forbidden("A fresh, approved, purpose-bound single-use proof is required.", "step_up_required"));
        }

        return await clients.DeleteAsync(proof, clientId, cancellationToken) switch
        {
            Done => Results.NoContent(),
            NotFound notFound => ErrorResults.ToResult(notFound),
            Conflict conflict => ErrorResults.ToResult(conflict),
            Forbidden forbidden => ErrorResults.ToResult(forbidden),
        };
    }
}
