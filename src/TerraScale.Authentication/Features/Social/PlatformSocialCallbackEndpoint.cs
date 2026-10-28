using TerraScale.Authentication.Features.Oidc;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Social;

/// <summary>The platform OAuth callback registered at GitHub and Google.</summary>
public sealed class PlatformSocialCallbackEndpoint(PlatformSocialSignIn social) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/signin/{provider}");
        Name("ui_platform_social_callback");
        Summary("Complete platform provider sign-in");
        Description("Consumes browser-bound OAuth state once, exchanges the authorization code with PKCE and resolves the verified stable provider subject. An unbound subject whose email belongs to an existing account is refused. Accounts with an active authenticator app get the hosted second-factor step before any session. No provider token is stored or returned.");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => new(social.CompleteAsync(this.HttpContext,
        OidcIssuer.InstanceId, (string)this.HttpContext.GetRouteValue("provider")!, cancellationToken));
}
