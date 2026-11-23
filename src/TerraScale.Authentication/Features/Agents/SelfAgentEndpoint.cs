using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Hosting;

namespace TerraScale.Authentication.Features.Agents;

public abstract class SelfAgentEndpoint(PlatformAuthentication authentication, AuthOptions options, Sessions sessions) : PlatformEndpoint(authentication)
{
    protected const string Root = "/v1/{project}/me/agents";

    protected override async Task<IResult> AuthenticateAndHandleAsync(CancellationToken cancellationToken)
    {
        var project = this.RouteId("project");
        var request = this.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method) && (request.Headers.Origin is not [{ } origin] || !SignInFlow.IsPublicOrigin(origin, options.PublicOrigin)))
        {
            return ErrorResults.ToResult(new Forbidden("Your role does not allow this.", "forbidden_role"));
        }

        return await sessions.ResolveAsync(project, request.Cookies[Tenancy.SessionCookie(project)], cancellationToken) is { } live
            ? await this.HandleAsync(live.User, cancellationToken)
            : TypedResults.Unauthorized();
    }

    protected void DescribeSelf(string operation, string summary, int status, Type? response)
    {
        this.Describe(operation, summary, "The signed-in end user manages only the agents they own. Uses the project's hosted session cookie; mutations require the auth public Origin.", "Agents",
            null, status, response, paged: response is not null, conflict: true);
        Metadata(new RequiresSecurity(SecuritySchemes.SessionCookie));
    }
}
