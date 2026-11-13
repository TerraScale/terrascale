using System.Security.Cryptography;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Security;
using TerraScale.Endpoints;
using ZLogger;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary><c>GET /ui/{slug}/social/{provider}/callback</c>: the provider's redirect back (Google, GitHub, OIDC).</summary>
public sealed class SocialCallbackEndpoint(SocialFlow flow) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/ui/{slug}/social/{provider}/callback");
        Name("ui_social_callback");
        Summary("Finish a social sign-in");
        Description(SocialFlow.CallbackDescription);
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var query = this.HttpContext.Request.Query;
        return await flow.CompleteAsync(this.HttpContext, query["code"], query["state"], cancellationToken);
    }
}

/// <summary>The callback logic both callback endpoints share.</summary>
public sealed class SocialFlow(SocialSignIn social, Users users, Sessions sessions, TotpCredentials mfa, HostedAuth hosted, ILogger<SocialFlow> logger)
{
    internal const string CallbackDescription = "The redirect URI registered at the provider; Apple posts it (form_post). Needs the start's state cookie and matching state, then redeems the code with the project's stored secret. The validated external subject selects its bound account. An unknown subject creates an account immediately only with an unused verified email when the project allows sign-up; matching email never links accounts. An account with an active authenticator app gets the hosted second-factor step before any session. Success sets the project's session cookie and continues to next; any failure answers 401 without a session. Unverified email uses a sealed ten-minute pending link and a single-use emailed code for new and existing accounts. Proof atomically creates an allowed account and connects the subject. Shares the hosted IP and sign-up budgets.";

    /// <summary>Redeems the provider's answer and signs the browser in.</summary>
    public async Task<IResult> CompleteAsync(HttpContext context, string? code, string? state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await ProviderAsync(context, cancellationToken) is not { } provider)
        {
            return Results.NotFound();
        }

        var tenant = SignInFlow.Tenant(context);
        if (await hosted.LimitIpAsync(SignInFlow.Ip(context), mails: false, cancellationToken) is { } wait)
        {
            return SignInFlow.Limited(context, SignInStep.Email, wait, string.Empty, null);
        }

        // Clear browser state on every attempt; the provider independently enforces single-use code redemption.
        var resumed = social.Resume(tenant, provider.Provider, context.Request.Cookies[SocialSignIn.Cookie(tenant)], state);
        context.Response.Cookies.Delete(SocialSignIn.Cookie(tenant), CookieOptions(tenant));
        if (resumed is null || string.IsNullOrEmpty(code) || code.Length > 4096)
        {
            AuthMetrics.SignIn("hosted_social", "refused");
            return Failed(context, null, StatusCodes.Status401Unauthorized, "This sign-in expired or was started in another browser. Start again.");
        }

        SocialIdentity? identity;
        try
        {
            identity = await social.CompleteAsync(tenant, provider, resumed, code, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or CryptographicException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning($"Hosted {provider.Provider:@Provider} sign-in for project {tenant:@ProjectId} failed at the provider: {error.GetType().Name:@Error}");
            identity = null;
        }

        if (identity is not null && await ProviderAsync(context, cancellationToken) != provider)
        {
            identity = null;
        }

        if (identity is not null && resumed.Link is { } link)
        {
            return await this.CompleteLinkAsync(context, provider, identity, link, cancellationToken);
        }

        var external = identity is null ? null : new LinkedProvider(provider.Provider, identity.Issuer, identity.ClientId, identity.Subject);
        var user = external is null ? null : await users.FindByProviderAsync(tenant, external, cancellationToken);
        if (user is null && identity is { EmailVerified: true })
        {
            user = await users.FromProviderAsync(tenant, external!, identity.Email, cancellationToken);
        }

        if (user is null && identity is { EmailVerified: false } && Users.NormalizeEmail(identity.Email) is { } email)
        {
            using var reservation = context.RequestServices.GetRequiredService<SignInMail>().Reserve();
            if ((await hosted.LimitSocialSignUpAsync(SignInFlow.Ip(context), cancellationToken)
                ?? await hosted.LimitMailAsync(email, SignInFlow.Ip(context), cancellationToken)) is { } refusal)
            {
                return SignInFlow.Limited(context, SignInStep.Code, refusal.Wait, email, resumed.Next);
            }

            var clock = context.RequestServices.GetRequiredService<TimeProvider>();
            var options = context.RequestServices.GetRequiredService<AuthOptions>();
            var pending = new PendingSocialLink(Tokens.RandomToken(32), email, external!, SocialSignIn.ConfigurationHash(provider),
                (clock.GetUtcNow() + PendingSocialLinks.Lifetime).ToUnixTimeSeconds());
            var token = PendingSocialLinks.Seal(options, tenant, pending);
            if (!PendingSocialLinks.Write(context, tenant, token))
            {
                return Failed(context, resumed.Next, StatusCodes.Status400BadRequest, "The provider returned an identity that is too large to connect. Use another sign-in method.");
            }

            await reservation.EnqueueSocialAsync(tenant, email, token, SocialProviders.Label(provider.Provider), AuthEmails.Locale(context.Request));
            return SignInPageModel.Render(context, new SignInPageModel(SignInStep.Code, tenant, email, resumed.Next)
            { CodeRequest = "sign-in", LinkProvider = SocialProviders.Label(provider.Provider) });
        }

        // An active local factor gets the hosted second step; the provider's own amr/acr never count as one.
        if (user is not null && await SignInFlow.SecondFactorAsync(context, mfa, user, resumed.Next, cancellationToken) is { } factor)
        {
            return factor;
        }

        if (user is null || await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is not { } session)
        {
            AuthMetrics.SignIn("hosted_social", "refused");
            return Failed(context, resumed.Next, StatusCodes.Status401Unauthorized, $"{SocialProviders.Label(provider.Provider)} could not complete this sign-in. Sign in with your password, then connect {SocialProviders.Label(provider.Provider)} from Security.");
        }

        AuthMetrics.SignIn("hosted_social", "succeeded");
        return await SignInFlow.SignedInAsync(context, sessions, session, resumed.Next, cancellationToken);
    }

    private async Task<IResult> CompleteLinkAsync(HttpContext context, SocialProviderRecord provider, SocialIdentity identity, SocialLink link,
        CancellationToken cancellationToken)
    {
        var live = await SecurityFlow.LiveAsync(context, sessions, cancellationToken);
        var linked = live is { } state && state.Session.TokenHash == link.SessionHash && state.User.Id == link.UserId
            && state.User.AuthStamp == link.AuthStamp && state.User.AuthEpoch == link.AuthEpoch
            ? await users.LinkProviderAsync(state.User,
                new LinkedProvider(provider.Provider, identity.Issuer, identity.ClientId, identity.Subject), cancellationToken)
            : null;
        if (linked is true)
        {
            AuthMetrics.SignIn("hosted_social", "succeeded");
            return Results.Redirect(SignInFlow.SecurityPath(SignInFlow.Tenant(context)));
        }

        var label = SocialProviders.Label(provider.Provider);
        if (live is null)
        {
            AuthMetrics.SignIn("hosted_social", "refused");
            return SecurityFlow.SignIn(context);
        }

        AuthMetrics.SignIn("hosted_social", "refused");
        return await SecurityFlow.RefusedAsync(context, sessions, mfa, live.Value.User, linked is false
            ? ("Provider already connected", $"This {label} account is already connected to another account.")
            : ("Connect expired", $"Your account or session changed. Connect {label} again from Security."),
            linked is false ? StatusCodes.Status409Conflict : StatusCodes.Status401Unauthorized, cancellationToken);
    }

    /// <summary>The page's configured provider, or null for a wrong slug, the platform or an unconfigured provider.</summary>
    internal static async Task<SocialProviderRecord?> ProviderAsync(HttpContext context, CancellationToken cancellationToken) =>
        !SignInFlow.WrongSlug(context) && Tenancy.IsProject(SignInFlow.Tenant(context)) && context.GetRouteValue("provider") is string name
            ? (await context.RequestServices.GetRequiredService<ProjectAuthSettings>().GetAsync(SignInFlow.Tenant(context), cancellationToken)).Provider(name)
            : null;

    /// <summary>The state cookie: sent back on Apple's cross-site form post, so SameSite=None, and only to the social pages.</summary>
    internal static CookieOptions CookieOptions(string tenant) => new()
    {
        Path = $"/ui/{tenant}/social",
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.None,
        IsEssential = true,
        MaxAge = SocialSignIn.Lifetime,
    };

    internal static IResult Failed(HttpContext context, string? next, int status, string message) =>
        SignInPageModel.Render(context, new SignInPageModel(SignInStep.Email, SignInFlow.Tenant(context), Next: next, Error: message) { ErrorTitle = "Sign-in failed" }, status);
}
