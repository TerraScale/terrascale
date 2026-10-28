using System.Security.Cryptography;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Hosting;
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
    internal const string CallbackDescription = "The redirect URI registered at the provider; Apple posts it (form_post). Needs the start's state cookie and matching state, then redeems the code with the project's stored secret. The validated external subject selects its bound account. An unknown subject creates an account only with an unused verified email when the project allows sign-up; matching email never links accounts. An account with an active authenticator app gets the hosted second-factor step before any session. Success sets the project's session cookie and continues to next; any failure answers 401 without a session. Shares the hosted IP budget.";

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

        var user = identity is null ? null : await users.FromProviderAsync(tenant,
            new LinkedProvider(provider.Provider, identity.Issuer, identity.ClientId, identity.Subject),
            identity.Email, HostedTheme.Of(context).SignUp, cancellationToken);

        // An active local factor gets the hosted second step; the provider's own amr/acr never count as one.
        if (user is not null && await SignInFlow.SecondFactorAsync(context, mfa, user, resumed.Next, cancellationToken) is { } factor)
        {
            return factor;
        }

        if (user is null || await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is not { } session)
        {
            AuthMetrics.SignIn("hosted_social", "refused");
            return Failed(context, resumed.Next, StatusCodes.Status401Unauthorized, $"{SocialProviders.Label(provider.Provider)} could not complete this sign-in. Try an existing sign-in method; connecting another method requires proof of your existing account.");
        }

        AuthMetrics.SignIn("hosted_social", "succeeded");
        return await SignInFlow.SignedInAsync(context, sessions, session, resumed.Next, cancellationToken);
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
