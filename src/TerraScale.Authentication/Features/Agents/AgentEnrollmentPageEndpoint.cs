using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Agents;

public sealed class AgentEnrollmentPageEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(AgentEnrollmentHosted.Route);
        Name("ui_agent_enrollment");
        Summary("Approve an AI agent");
        Description("The verification page of agent enrollment, branded for the project. Without a live project session it redirects to /ui/{slug}/sign-in and returns with the same user_code. With user_code it shows the agent's name, the requested scopes the agent policy allows, whether it asks to act as itself, its key fingerprint and the signed-in account, with Approve and Deny; opening it never decides. Each code lookup charges the hosted IP budget and 20 per user per 15 minutes (429 with Retry-After).");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status302Found);
        Produces<string>(StatusCodes.Status400BadRequest, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await AgentEnrollmentHosted.HandleAsync(this.HttpContext, post: false, cancellationToken);
}
