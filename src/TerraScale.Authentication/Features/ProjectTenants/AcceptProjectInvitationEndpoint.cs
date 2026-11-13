using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class AcceptProjectInvitationEndpoint(ProjectInvitations invitations) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/ui/{slug}/invitations/{token}");
        Name("ui_project_invitation");
        Summary("Accept a project invitation");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces(StatusCodes.Status404NotFound);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(this.HttpContext)
            || await invitations.LookupAsync(SignInFlow.Tenant(this.HttpContext), this.HttpContext.GetRouteValue("token") as string ?? "", cancellationToken) is not { } invitation)
        {
            return Results.NotFound();
        }

        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignUp, invitation.ProjectId, invitation.Email,
            SignInFlow.SafeNext(this.HttpContext, invitation.RedirectUrl)));
    }
}
