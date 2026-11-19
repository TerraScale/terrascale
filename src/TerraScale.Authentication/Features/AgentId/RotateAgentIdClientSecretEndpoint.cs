using Microsoft.AspNetCore.Http.Features;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class RotateAgentIdClientSecretEndpoint(PlatformAuthentication authentication, AgentIdClients clients, StepUpProofs proofs) : AgentIdClientEndpoint(authentication)
{
    public override void Configure()
    {
        Post(Root + "/{client_id}/secret/rotate");
        this.Describe("rotate_agentid_client_secret", "Rotate a Sign in as agent client's secret",
            "Issues a new client_secret for a client the signed-in account owns and answers it exactly once; only its digest is kept. The previous secret keeps working for grace_seconds (default 3600, 0 to 86400), then is refused like any wrong secret; rotating again inside the grace replaces the previous secret. Another account's client is 404. Needs a rotate_oauth_client step-up proof in X-TerraScale-Step-Up-Proof, spent in the same save as the change, so a sign-out everywhere that commits first refuses it with 409; a token an agent acts with is 403 owner_required.",
            "Sign in as agent", typeof(RotateAgentIdClientRequest), StatusCodes.Status200OK, typeof(AgentIdClientSecretBody), conflict: true);
    }

    protected override async Task<IResult> HandleOwnerAsync(UserRecord owner, CancellationToken cancellationToken)
    {
        var sent = this.HttpContext.Request;
        var empty = sent.ContentLength is 0
            || sent.ContentLength is null && this.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true;
        var body = empty ? new RotateAgentIdClientRequest(null) : await this.BodyAsync(AgentIdClientJson.Default.RotateAgentIdClientRequest, cancellationToken);
        var grace = body.GraceSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : AgentIdClients.DefaultGrace;
        if (grace < TimeSpan.Zero || grace > AgentIdClients.MaxGrace)
        {
            throw Invalid("grace_seconds must be 0 to 86400 seconds.");
        }

        var clientId = this.RouteId("client_id");
        var proof = await proofs.PrepareHeaderAsync(owner, sent, "rotate_oauth_client", null, cancellationToken) ?? throw StepUpProofs.Required();
        return Results.Json(await clients.RotateAsync(proof, clientId, grace, cancellationToken), AgentIdClientJson.Default.AgentIdClientSecretBody);
    }
}
