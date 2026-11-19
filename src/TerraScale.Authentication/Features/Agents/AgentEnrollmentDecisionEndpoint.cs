using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Agents;

public sealed class AgentEnrollmentDecisionEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post(AgentEnrollmentHosted.Route);
        Name("ui_agent_enrollment_decide");
        Summary("Approve or deny an AI agent");
        Description("Same-origin form post with user_code and decision=approve or deny from a browser with a live project session (otherwise 403). Approve needs a fresh approve_agent step-up bound to the enrollment: without one it redirects to /ui/{slug}/step-up/{id} and back here. It then creates the agent owned by the signed-in user with the requested scopes the agent policy allows, under the create_agent limits and events, and the code stops working. Deny makes the agent's next poll answer access_denied. A code works once; an unknown, expired or decided code answers 400.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status302Found);
        Produces<string>(StatusCodes.Status400BadRequest, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status409Conflict, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await AgentEnrollmentHosted.HandleAsync(this.HttpContext, post: true, cancellationToken);
}
