using TerraScale.Authentication.Features.SignIn;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Social;

/// <summary>The project-isolated callback advertised by provider management.</summary>
public sealed class ProjectSocialCallbackEndpoint(SocialSignIn social) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/ui/{slug}/social/{provider}/callback");
        Name("ui_project_social_callback");
        Summary("Complete project provider sign-in");
        Description("Uses only the resolved project's credentials and accounts; consumes browser-bound OAuth state once and links a verified provider email. Never uses platform credentials.");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces(StatusCodes.Status404NotFound);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => SignInFlow.WrongSlug(this.HttpContext)
        ? ValueTask.FromResult<IResult>(Results.NotFound())
        : new(social.CompleteAsync(this.HttpContext, SignInFlow.Tenant(this.HttpContext), (string)this.HttpContext.GetRouteValue("provider")!, cancellationToken));
}
