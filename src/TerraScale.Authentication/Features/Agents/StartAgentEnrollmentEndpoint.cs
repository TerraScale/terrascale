using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;

namespace TerraScale.Authentication.Features.Agents;

public sealed class StartAgentEnrollmentEndpoint(PlatformAuthentication authentication, AgentEnrollments enrollments) : AgentEnrollmentEndpoint(authentication)
{
    public override void Configure()
    {
        Post(Root);
        this.DescribeEnrollment("start_agent_enrollment", "Start agent enrollment",
            "Unauthenticated. An agent asks to be enrolled with its own public P-256 JWK (the same rules as create_agent). Answers enrollment_id, a single-use user_code, verification_uri (the project's hosted approval page), verification_uri_complete, expires_in (600) and interval (5). The project user who will own the agent signs in there, verifies again and approves or denies. Limited to 10 per IP per 15 minutes and 300 per project per hour (429 with Retry-After). A storage conflict answers 409 version_conflict; start again.",
            typeof(StartAgentEnrollmentRequest), StatusCodes.Status201Created, typeof(AgentEnrollmentBody), conflict: true);
    }

    protected override async Task<IResult> HandleAnonymousAsync(CancellationToken cancellationToken)
    {
        var project = this.TenantId();
        var request = await this.BodyAsync(AgentEnrollmentJson.Default.StartAgentEnrollmentRequest, cancellationToken);
        return Results.Json(await enrollments.StartAsync(project, SignInFlow.Ip(this.HttpContext), request, cancellationToken), AgentEnrollmentJson.Default.AgentEnrollmentBody,
            statusCode: StatusCodes.Status201Created);
    }
}
