using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class ProjectWaitlistPageEndpoint(ProjectAuthSettings settings) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/ui/{slug}/waitlist");
        Name("ui_project_waitlist");
        Summary("Join a project's waitlist");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(this.HttpContext)
            || (await settings.GetAsync(SignInFlow.Tenant(this.HttpContext), cancellationToken)).SignUpMode != "waitlist")
        {
            return Results.NotFound();
        }

        var nonce = BrowserSecurity.NewNonce();
        BrowserSecurity.ApplyPageHeaders(this.HttpContext.Response, nonce);
        return WaitlistPage.Create(new(SignInFlow.Tenant(this.HttpContext), HostedTheme.Of(this.HttpContext), nonce));
    }
}
