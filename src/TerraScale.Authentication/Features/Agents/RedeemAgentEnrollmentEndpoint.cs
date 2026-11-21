using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Agents;

public sealed class RedeemAgentEnrollmentEndpoint(PlatformAuthentication authentication, AgentEnrollments enrollments) : AgentEnrollmentEndpoint(authentication)
{
    public override void Configure()
    {
        Post(Root + "/{enrollment}/token");
        this.DescribeEnrollment("redeem_agent_enrollment", "Poll agent enrollment",
            "Unauthenticated; proof is an ES256 JWT signed by the enrolled private key with aud this exact URL, a jti, iat, and exp at most 300 seconds ahead, each iat later than the previous accepted poll's. Without such a proof the answer is 401 invalid_proof and reveals nothing. Otherwise 400 authorization_pending, slow_down (sooner than interval), access_denied or expired_token, or 200 with agent_id and kid once approved. The result is answered once; later polls answer expired_token.",
            typeof(AgentEnrollmentTokenRequest), StatusCodes.Status200OK, typeof(AgentEnrollmentTokenBody), conflict: false);
    }

    protected override async Task<IResult> HandleAnonymousAsync(CancellationToken cancellationToken)
    {
        var project = this.TenantId();
        var enrollment = this.RouteId("enrollment");
        var request = await this.BodyAsync(AgentEnrollmentJson.Default.AgentEnrollmentTokenRequest, cancellationToken);
        return await enrollments.RedeemAsync(project, enrollment, request.Proof, cancellationToken) switch
        {
            AgentEnrollmentTokenBody token => Results.Json(token, AgentEnrollmentJson.Default.AgentEnrollmentTokenBody),
            Unauthorized unauthorized => ErrorResults.ToResult(unauthorized),
            Validation invalid => ErrorResults.ToResult(invalid),
        };
    }
}
