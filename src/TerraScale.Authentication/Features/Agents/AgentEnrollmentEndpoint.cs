using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Agents;

public abstract class AgentEnrollmentEndpoint(PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    protected const string Root = "/v1/{project}/agent-enrollments";

    protected override Task<IResult> AuthenticateAndHandleAsync(CancellationToken cancellationToken) => this.HandleAnonymousAsync(cancellationToken);

    protected sealed override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Agent enrollment endpoints are anonymous.");

    protected abstract Task<IResult> HandleAnonymousAsync(CancellationToken cancellationToken);

    protected void DescribeEnrollment(string operation, string summary, string description, Type request, int status, Type response, bool conflict)
    {
        this.Describe(operation, summary, description, "Agents", request, status, response, conflict: conflict, roleChecked: false);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        AllowAnonymous();
    }
}
