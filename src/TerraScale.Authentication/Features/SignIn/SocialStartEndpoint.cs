using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary><c>GET /ui/{slug}/social/{provider}</c>: sends the browser to the project's provider to sign in.</summary>
public sealed class SocialStartEndpoint(SocialSignIn social, HostedAuth hosted) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/ui/{slug}/social/{provider}");
        Name("ui_social_start");
        Summary("Sign in with a social provider");
        Description("Redirects to the project's configured google, github, apple or oidc provider with state, PKCE (not Apple) and an OIDC nonce, bound to the browser by an encrypted 10-minute cookie. A provider the project did not configure, and the platform's pages, answer 404. Shares the hosted IP budget (429 with Retry-After). next is honoured as on the sign-in page.");
        Tags("Sign-in");
        Produces(StatusCodes.Status302Found);
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<string>(StatusCodes.Status502BadGateway, "text/html");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SocialFlow.ProviderAsync(this.HttpContext, cancellationToken) is not { } provider)
        {
            return Results.NotFound();
        }

        var tenant = SignInFlow.Tenant(this.HttpContext);
        var next = SignInFlow.SafeNext(this.HttpContext, this.HttpContext.Request.Query["next"]);
        if (await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken) is { } wait)
        {
            return SignInFlow.Limited(this.HttpContext, SignInStep.Email, wait, string.Empty, next);
        }

        try
        {
            var (location, sealedState) = await social.StartAsync(tenant, provider, next, cancellationToken);
            this.HttpContext.Response.Cookies.Append(SocialSignIn.Cookie(tenant), sealedState, SocialFlow.CookieOptions(tenant));
            return Results.Redirect(location.AbsoluteUri);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return SocialFlow.Failed(this.HttpContext, next, StatusCodes.Status502BadGateway, "The sign-in provider could not be reached. Try again, or use another method.");
        }
    }
}
