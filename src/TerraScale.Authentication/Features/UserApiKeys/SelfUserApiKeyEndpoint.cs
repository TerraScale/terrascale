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

    protected UserApiKeyAuthorization? CreationAuthorization { get; private set; }

    protected virtual bool RequiresRecentSignIn => false;

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
        if (await sessions.ResolveVersionedAsync(project, request.Cookies[Tenancy.SessionCookie(project)], cancellationToken) is not { } live)
        {
            return Results.Unauthorized();
        }

        if (this.RequiresRecentSignIn)
        {
            if (!SecurityFlow.Recent(this.HttpContext, live.Session, SecurityFlow.FactorEnrollmentWindow))
            {
                throw new ApiException(403, "reauthentication_required", "Sign in again to create an API key.");
            }

            this.CreationAuthorization = new(Sessions.Live(live.Session, live.Version, this.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()),
                live.User.AuthEpoch, live.User.AuthStamp ?? throw new InvalidDataException("The user has no authentication stamp."), live.Session.CreatedAt + SecurityFlow.FactorEnrollmentWindow < live.Session.ExpiresAt
                    ? live.Session.CreatedAt + SecurityFlow.FactorEnrollmentWindow : live.Session.ExpiresAt);
        }

        return await this.HandleAsync(live.User, cancellationToken);
    }

    protected void DescribeSelf(string operation, string summary, Type? request, int status, Type? response)
    {
        this.Describe(operation, summary, "The signed-in end user manages only their own keys. Uses the project's hosted session cookie; mutations require the auth public Origin. Creation requires sign-in within 10 minutes and an expiry at most 365 days ahead (default 90 days).", "User API keys",
            request, status, response, conflict: true);
        Metadata(new RequiresSecurity(SecuritySchemes.SessionCookie));
    }
}
