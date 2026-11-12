using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Hosting;

namespace TerraScale.Authentication.Features.UserApiKeys;

public abstract class SelfUserApiKeyEndpoint(PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    protected const string Root = "/v1/{project}/me/api-keys";

    protected override async Task<IResult> AuthenticateAndHandleAsync(CancellationToken cancellationToken)
    {
        var project = this.RouteId("project");
        var request = this.HttpContext.Request;
        var options = this.HttpContext.RequestServices.GetRequiredService<AuthOptions>();
        if (!HttpMethods.IsGet(request.Method) && (request.Headers.Origin is not [{ } origin] || !SignInFlow.IsPublicOrigin(origin, options.PublicOrigin)))
        {
            throw PlatformDirectory.Forbidden();
        }

        var sessions = this.HttpContext.RequestServices.GetRequiredService<Sessions>();
        if (await sessions.ResolveAsync(project, request.Cookies[Tenancy.SessionCookie(project)], cancellationToken) is not { } live)
        {
            return Results.Unauthorized();
        }

        return await this.HandleAsync(live.User, cancellationToken);
    }

    protected void DescribeSelf(string operation, string summary, Type? request, int status, Type? response)
    {
        this.Describe(operation, summary, "The signed-in end user manages only their own keys. Uses the project's hosted session cookie; mutations require the auth public Origin.", "User API keys",
            request, status, response, conflict: true);
        Metadata(new RequiresSecurity(SecuritySchemes.SessionCookie));
    }
}
