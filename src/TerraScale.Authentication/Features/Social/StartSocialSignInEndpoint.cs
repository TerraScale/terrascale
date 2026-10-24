using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Social;

/// <summary>Starts same-origin provider sign-in, with a fresh browser binding and PKCE verifier.</summary>
public sealed class StartSocialSignInEndpoint(SocialSignIn social, HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/social/{provider}");
        Name("ui_social_start");
        Summary("Continue with GitHub or Google");
        Description("Same-origin form post. Redirects only when this tenant has provider credentials; state is browser-bound and expires in ten minutes. next accepts only this tenant's authorization or device page.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces(StatusCodes.Status302Found);
        Produces(StatusCodes.Status404NotFound);
        Produces(StatusCodes.Status403Forbidden);
        Produces(StatusCodes.Status429TooManyRequests);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        return await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken) is { } wait
            ? HostedAuth.TooMany(wait)
            : await social.StartAsync(this.HttpContext, (string)this.HttpContext.GetRouteValue("provider")!, form.Next, cancellationToken);
    }
}
