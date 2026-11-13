using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class JoinProjectWaitlistEndpoint(AuthOptions options, SignUpRestrictions restrictions, HostedAuth hosted) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/waitlist");
        Name("ui_project_waitlist_join");
        Summary("Request access to a project");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(this.HttpContext))
        {
            return Results.NotFound();
        }

        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        if (await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), false, cancellationToken) is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        await restrictions.JoinAsync(SignInFlow.Tenant(this.HttpContext), form.Email ?? "", cancellationToken);
        var nonce = BrowserSecurity.NewNonce();
        BrowserSecurity.ApplyPageHeaders(this.HttpContext.Response, nonce);
        return WaitlistPage.Create(new(SignInFlow.Tenant(this.HttpContext), HostedTheme.Of(this.HttpContext), nonce, true));
    }
}
