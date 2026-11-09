using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class SocialConnectEndpoint(SocialSignIn social, Sessions sessions, HostedAuth hosted, AuthOptions options, ProjectAuthSettings settings)
    : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/security/social/connect");
        Name("ui_security_social_connect");
        Summary("Connect a sign-in provider");
        Description("Same-origin form post with provider from a live session opened in the last 15 minutes; an older session is asked to sign in again. Redirects to the project's configured provider with a state bound to this account, session and authentication state, sealed into the encrypted 10-minute social cookie. The callback links the identity only to this account, refuses one owned by any account in the project, and returns to the security page; it never signs an account in and never links by email. A provider the project did not configure, and the platform's pages, answer 404. Shares the hosted IP budget (429 with Retry-After).");
        Tags("Security");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces(StatusCodes.Status302Found);
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<string>(StatusCodes.Status502BadGateway, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(this.HttpContext) || !Tenancy.IsProject(SignInFlow.Tenant(this.HttpContext)))
        {
            return Results.NotFound();
        }

        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var tenant = SignInFlow.Tenant(this.HttpContext);
        var configured = (await settings.GetAsync(tenant, cancellationToken)).Provider(this.HttpContext.Request.Form["provider"].ToString());
        if (configured is null)
        {
            return Results.NotFound();
        }

        if (await SecurityFlow.LiveAsync(this.HttpContext, sessions, cancellationToken) is not { } live)
        {
            return SecurityFlow.SignIn(this.HttpContext);
        }

        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        if (!SecurityFlow.Recent(this.HttpContext, live.Session))
        {
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Reauthenticate, tenant, Next: SignInFlow.SecurityPath(tenant))
            { Account = live.User.Email }, StatusCodes.Status401Unauthorized);
        }

        if (live.User.LinkedProviders.Any(identity => identity.Provider == configured.Provider))
        {
            return Results.Redirect(SignInFlow.SecurityPath(tenant));
        }

        if (await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken) is { } wait)
        {
            return SignInFlow.Limited(this.HttpContext, SignInStep.Email, wait, string.Empty, next);
        }

        try
        {
            var (location, sealedState) = await social.StartAsync(tenant, configured, next, cancellationToken,
                new SocialLink(live.User.Id, live.Session.TokenHash, live.User.AuthStamp, live.User.AuthEpoch));
            this.HttpContext.Response.Cookies.Append(SocialSignIn.Cookie(tenant), sealedState, SocialFlow.CookieOptions(tenant));
            return Results.Redirect(location.AbsoluteUri);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return SocialFlow.Failed(this.HttpContext, next, StatusCodes.Status502BadGateway, "The sign-in provider could not be reached. Try again, or use another method.");
        }
    }
}

public sealed class SocialUnlinkEndpoint(Sessions sessions, Users users, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/security/social/unlink");
        Name("ui_security_social_unlink");
        Summary("Disconnect a sign-in provider");
        Description("Same-origin form post with provider from a live session opened in the last 15 minutes. Success releases the provider's identities from the account, revokes every prior session and token, opens a fresh session for this browser and returns to the security page. A provider the account does not have redirects back without a change.");
        Tags("Security");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces(StatusCodes.Status302Found);
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status409Conflict, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        if (await SecurityFlow.LiveAsync(this.HttpContext, sessions, cancellationToken) is not { } live)
        {
            return SecurityFlow.SignIn(this.HttpContext);
        }

        var tenant = SignInFlow.Tenant(this.HttpContext);
        var provider = this.HttpContext.Request.Form["provider"].ToString();
        if (!SecurityFlow.Recent(this.HttpContext, live.Session))
        {
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Reauthenticate, tenant, Next: SignInFlow.SecurityPath(tenant))
            { Account = live.User.Email }, StatusCodes.Status401Unauthorized);
        }

        if (!live.User.LinkedProviders.Any(identity => identity.Provider == provider))
        {
            return Results.Redirect(SignInFlow.SecurityPath(tenant));
        }

        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        if (!await users.UnlinkProviderAsync(live.User, provider, cancellationToken))
        {
            return await SecurityFlow.RefusedAsync(this.HttpContext, sessions,
                this.HttpContext.RequestServices.GetRequiredService<Features.Mfa.TotpCredentials>(), live.User,
                ("Provider not disconnected", "Your account changed. Reload and try again."), StatusCodes.Status409Conflict, cancellationToken);
        }

        if (await users.GetAsync(tenant, live.User.Id, cancellationToken) is { } updated
            && await SignInFlow.OpenSessionAsync(sessions, updated, cancellationToken) is { } session)
        {
            BrowserSecurity.SetSessionCookie(this.HttpContext.Response, session.Token, tenant);
        }

        return await SecurityFlow.PageAsync(this.HttpContext,
            this.HttpContext.RequestServices.GetRequiredService<Features.Mfa.TotpCredentials>(), live.User, null, cancellationToken,
            notice: $"{SocialProviders.Label(provider)} is disconnected.", next: next);
    }
}
