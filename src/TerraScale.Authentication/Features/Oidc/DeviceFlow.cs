using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Metadata;
using System.Security.Claims;
using OpenIddict.Abstractions;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

public static class DeviceFlow
{
    public const string PagePath = "/ui/" + OidcIssuer.Slug + "/device";

    public static readonly TimeSpan Lifetime = Passcodes.Lifetime;

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public const int LookupsPerUser = 20;

    public static readonly TimeSpan LookupWindow = TimeSpan.FromMinutes(15);

    internal static async Task<IResult> PageAsync(HttpContext http, TotpCredentials mfa, AuthOptions options, IOpenIddictApplicationManager applications,
        CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(http) || http.Items[DeviceGate.Key] is not DeviceGate.Result gate)
        {
            return TypedResults.NotFound();
        }

        var post = HttpMethods.IsPost(http.Request.Method);
        if (gate.Outcome == DeviceGate.Outcome.NoSession && !post)
        {
            return TypedResults.Redirect($"/ui/{OidcIssuer.Slug}/sign-in?next={Uri.EscapeDataString(PagePath + http.Request.QueryString)}");
        }

        if (gate.Outcome is DeviceGate.Outcome.NoSession or DeviceGate.Outcome.CrossOrigin)
        {
            return SignInFlow.Refused(http);
        }

        if (gate.Outcome == DeviceGate.Outcome.Limited)
        {
            return SignInFlow.Limited(http, SignInStep.DeviceEnter, gate.Wait, string.Empty, null);
        }

        var (session, user) = gate.Live;
        var code = (post ? http.Request.Form["user_code"] : http.Request.Query["user_code"]).ToString().Trim();
        var page = new SignInPageModel(SignInStep.DeviceEnter, OidcIssuer.Slug) { Account = user.Email, UserCode = code };
        if (code.Length == 0)
        {
            return SignInPageModel.Render(http, page);
        }

        if ((await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal is not { } device
            || string.IsNullOrEmpty(device.GetClaim(Claims.ClientId)))
        {
            return SignInPageModel.Render(http, page with { Error = "Check the code in your terminal. Codes expire after 30 minutes.", ErrorTitle = "That code is wrong or expired" },
                StatusCodes.Status400BadRequest);
        }

        var decision = post ? http.Request.Form["decision"].ToString() : string.Empty;
        if (decision == "deny")
        {
            return TypedResults.Forbid(OidcResults.Error(OidcErrors.AccessDenied, "The user denied the device."), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        if (decision != "approve")
        {
            return SignInPageModel.Render(http, await ConfirmPageAsync(http, page, device, applications, cancellationToken),
                post ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK);
        }

        if (!user.MultiFactor && !await mfa.AllowsPrimaryAuthenticationAsync(user, cancellationToken))
        {
            return SignInPageModel.Render(http, await ConfirmPageAsync(http, page, device, applications, cancellationToken) with
            {
                ErrorTitle = "Two-factor accounts cannot sign in the CLI yet",
                Error = "This account has two-factor authentication, which the device flow does not support yet.",
            }, StatusCodes.Status403Forbidden);
        }

        return OidcResults.SignIn(options, user, session.CreatedAt, device.GetScopes(), OidcIssuer.SignInAudiences(options));
    }

    private static async Task<SignInPageModel> ConfirmPageAsync(HttpContext http, SignInPageModel page, ClaimsPrincipal device,
        IOpenIddictApplicationManager applications, CancellationToken cancellationToken)
    {
        var clientId = device.GetClaim(Claims.ClientId) ?? string.Empty;
        var name = await applications.FindByClientIdAsync(clientId, cancellationToken) is { } application
            ? await applications.GetDisplayNameAsync(application, cancellationToken)
            : null;
        return page with
        {
            Step = SignInStep.DeviceConfirm,
            ClientName = string.IsNullOrEmpty(name) ? clientId : name,
            Scopes = [.. device.GetScopes().Select(ScopeLabel)],
        };
    }

    private static string ScopeLabel(string scope) => scope switch
    {
        Scopes.OpenId => "Your account id",
        Scopes.Email => "Your email address",
        Scopes.Profile => "Your name",
        Scopes.OfflineAccess => "Stay signed in",
        _ => scope,
    };
}

public sealed class DeviceGate(Sessions sessions, HostedAuth hosted, AttemptLimiter limiter, AuthOptions options) : IOpenIddictServerHandler<ValidateEndUserVerificationRequestContext>
{
    public static readonly object Key = new();

    public enum Outcome
    {
        Pass,

        NoSession,

        CrossOrigin,

        Limited,
    }

    public sealed record Result(Outcome Outcome, (SessionRecord Session, UserRecord User) Live = default, TimeSpan Wait = default);

    public async ValueTask HandleAsync(ValidateEndUserVerificationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.Transaction.GetHttpRequest()?.HttpContext ?? throw new InvalidOperationException("OpenIddict ran outside an HTTP request.");
        if (HttpMethods.IsPost(http.Request.Method)
            && (http.Request.Headers.Origin is not [{ } origin] || !SignInFlow.IsPublicOrigin(origin, options.PublicOrigin)))
        {
            Skip(new Result(Outcome.CrossOrigin));
            return;
        }

        if (await sessions.ResolveAsync(OidcIssuer.InstanceId, http.Request.Cookies[SecuritySchemes.SessionCookieName], context.CancellationToken) is not { } live)
        {
            Skip(new Result(Outcome.NoSession));
            return;
        }

        if (!string.IsNullOrEmpty(context.Request.UserCode)
            && (await hosted.LimitIpAsync(SignInFlow.Ip(http), mails: false, context.CancellationToken)
                ?? await limiter.AcquireAsync(OidcIssuer.InstanceId, "device-code-user", live.User.Id, DeviceFlow.LookupsPerUser, DeviceFlow.LookupWindow, context.CancellationToken)) is { } wait)
        {
            Skip(new Result(Outcome.Limited, Wait: wait));
            return;
        }

        http.Items[Key] = new Result(Outcome.Pass, live);

        void Skip(Result result)
        {
            http.Items[Key] = result;
            context.SkipRequest();
        }
    }
}

public sealed class RenderDeviceDecision : IOpenIddictServerHandler<ApplyEndUserVerificationResponseContext>
{
    public async ValueTask HandleAsync(ApplyEndUserVerificationResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Transaction.GetHttpRequest()?.HttpContext is not { } http)
        {
            return;
        }

        var error = context.Response.Error;
        var page = error switch
        {
            null => new SignInPageModel(SignInStep.DeviceApproved, OidcIssuer.Slug),
            OidcErrors.AccessDenied => new SignInPageModel(SignInStep.DeviceDenied, OidcIssuer.Slug),
            _ => new SignInPageModel(SignInStep.DeviceEnter, OidcIssuer.Slug, Error: context.Response.ErrorDescription ?? "Start again from your terminal.")
            {
                ErrorTitle = "The device could not be approved",
            },
        };
        await SignInPageModel.Render(http, page, error is null or OidcErrors.AccessDenied ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest)
            .ExecuteAsync(http);
        context.HandleRequest();
    }
}

public sealed class AnnouncePollInterval : IOpenIddictServerHandler<ApplyDeviceAuthorizationResponseContext>
{
    public ValueTask HandleAsync(ApplyDeviceAuthorizationResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Response.Error is null)
        {
            context.Response["interval"] = (long)DeviceFlow.Interval.TotalSeconds;
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class PollNoFasterThanInterval(IOpenIddictTokenManager tokens, AttemptLimiter limiter) : IOpenIddictServerHandler<ProcessAuthenticationContext>
{
    public async ValueTask HandleAsync(ProcessAuthenticationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EndpointType is not OpenIddictServerEndpointType.Token || !context.Request.IsDeviceCodeGrantType()
            || context.Request.DeviceCode is not { Length: > 0 and <= 512 } code
            || await tokens.FindByReferenceIdAsync(code, context.CancellationToken) is not { } token
            || await tokens.GetIdAsync(token, context.CancellationToken) is not { } id)
        {
            return;
        }

        if (await limiter.AcquireCooldownAsync(OidcIssuer.InstanceId, "device-poll", id, DeviceFlow.Interval, context.CancellationToken) is not null)
        {
            context.Reject("slow_down", $"Poll at most every {DeviceFlow.Interval.TotalSeconds:0} seconds, and 5 seconds slower after each slow_down.");
        }
    }
}

public sealed class DeviceAuthorizationEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/{slug}/oauth/device");
        Name("oauth_device_authorization");
        Summary("Device authorization endpoint");
        Description("RFC 8628 for the public ts CLI client (client_id 00000000000000000000000007, display name ts-cli), application/x-www-form-urlencoded client_id and optional scope (openid email profile offline_access). Answers device_code, user_code, verification_uri ({public origin}/ui/terrascale/device), verification_uri_complete (adds ?user_code=), expires_in (1800) and interval (5). The user signs in and approves or denies on that page; the CLI polls the token endpoint with grant_type=urn:ietf:params:oauth:grant-type:device_code and gets authorization_pending, slow_down (polled sooner than interval), access_denied or expired_token as RFC 8628 §3.5 says. Calls share the token endpoint's per-IP minute budget; exhaustion answers 429 rate_limited with Retry-After.");
        Tags("OAuth");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<Dictionary<string, object>>();
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<OAuthErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IResult>(TypedResults.NotFound());
}

public sealed class DevicePageEndpoint(TotpCredentials mfa, AuthOptions options, IOpenIddictApplicationManager applications) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/ui/{slug}/device");
        Name("ui_device");
        Summary("Hosted device page");
        Description("The RFC 8628 verification page. Without a live tsauth_session cookie it redirects to /ui/{slug}/sign-in and returns here with the same user_code. With user_code it shows the code, the client and the signed-in account with Approve and Deny buttons; opening it never approves. Each code lookup charges the IP sign-in call bucket (120, refilling 30 per minute) and 20 per user per 15 minutes; exhaustion answers 429 with Retry-After.");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status302Found);
        Produces<string>(StatusCodes.Status400BadRequest, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DeviceFlow.PageAsync(this.HttpContext, mfa, options, applications, cancellationToken);
}

public sealed class DeviceDecisionEndpoint(TotpCredentials mfa, AuthOptions options, IOpenIddictApplicationManager applications) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/device");
        Name("ui_device_decide");
        Summary("Approve or deny a device");
        Description("Same-origin form post with user_code and decision=approve or deny, from a browser with a live session (otherwise 403). Approve signs the device in as the session's user with the scopes it asked for; an active MFA user's session must have proved the second factor (otherwise 403). Deny makes the device's next poll answer access_denied. A code works once; an unknown, expired or decided code answers 400. Lookups share the device page's limits (429 with Retry-After).");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status400BadRequest, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DeviceFlow.PageAsync(this.HttpContext, mfa, options, applications, cancellationToken);
}
