using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Metadata;
using OpenIddict.Abstractions;
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

/// <summary>
/// The RFC 8628 device flow of the ts CLI. OpenIddict issues the codes at <c>/{slug}/oauth/device</c> and redeems them
/// at the token endpoint; the user approves on ts-auth's own hosted page, <see cref="PagePath"/>, its end-user
/// verification endpoint. Device and user codes live 10 minutes (OpenIddict's default).
/// </summary>
public static class DeviceFlow
{
    /// <summary>The hosted verification page, under the public origin.</summary>
    public const string PagePath = "/ui/" + OidcIssuer.Slug + "/device";

    /// <summary>The minimum polling interval announced to the device and enforced with <c>slow_down</c>.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>User-code lookups one signed-in user may make per <see cref="LookupWindow"/>, beside the IP budget.</summary>
    public const int LookupsPerUser = 20;

    /// <summary>The per-user lookup window.</summary>
    public static readonly TimeSpan LookupWindow = TimeSpan.FromMinutes(15);

    internal static async Task<IResult> PageAsync(HttpContext http, TotpCredentials mfa, AuthOptions options, CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(http) || http.Items[DeviceGate.Key] is not DeviceGate.Result gate)
        {
            return Results.NotFound();
        }

        var post = HttpMethods.IsPost(http.Request.Method);
        if (gate.Outcome == DeviceGate.Outcome.NoSession && !post)
        {
            // Sign in first, then come back here with the code still filled in.
            return Results.Redirect($"/ui/{OidcIssuer.Slug}/sign-in?next={Uri.EscapeDataString(PagePath + http.Request.QueryString)}");
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

        // OpenIddict has looked the code up (the gate charged the lookup); an unknown, expired or decided one has no principal.
        if ((await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal is not { } device
            || string.IsNullOrEmpty(device.GetClaim(Claims.ClientId)))
        {
            return SignInPageModel.Render(http, page with { Error = "Check the code in your terminal. Codes expire after 10 minutes.", ErrorTitle = "That code is wrong or expired" },
                StatusCodes.Status400BadRequest);
        }

        // Opening the link (GET) only shows the code: approval is always an explicit, origin-checked POST.
        var decision = post ? http.Request.Form["decision"].ToString() : string.Empty;
        if (decision == "deny")
        {
            return Results.Forbid(OidcResults.Error(Errors.AccessDenied, "The user denied the device."), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        if (decision != "approve")
        {
            return SignInPageModel.Render(http, page with { Step = SignInStep.DeviceConfirm }, post ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK);
        }

        // The same rule as every other user grant: an active MFA factor refuses primary-only tokens.
        if (!user.MultiFactor && !await mfa.AllowsPrimaryAuthenticationAsync(user, cancellationToken))
        {
            return SignInPageModel.Render(http, page with
            {
                Step = SignInStep.DeviceConfirm,
                ErrorTitle = "Two-factor accounts cannot sign in the CLI yet",
                Error = "This account has two-factor authentication, which the device flow does not support yet.",
            }, StatusCodes.Status403Forbidden);
        }

        return OidcResults.SignIn(user, session.CreatedAt, device.GetScopes(), OidcIssuer.SignInAudiences(options));
    }
}

/// <summary>
/// Runs before OpenIddict looks a user code up on the device page: refuses cross-origin posts, sends browsers without a
/// session to sign in (no lookup happens for them), and charges each lookup to the IP's sign-in budget and to the user.
/// Refusals skip OpenIddict and leave the outcome for <see cref="DeviceFlow.PageAsync"/>.
/// </summary>
public sealed class DeviceGate(Sessions sessions, HostedAuth hosted, AttemptLimiter limiter, AuthOptions options) : IOpenIddictServerHandler<ValidateEndUserVerificationRequestContext>
{
    /// <summary>The <see cref="HttpContext.Items"/> key of the <see cref="Result"/>.</summary>
    public static readonly object Key = new();

    /// <summary>What the gate decided.</summary>
    public enum Outcome
    {
        /// <summary>OpenIddict may look the code up.</summary>
        Pass,

        /// <summary>No live hosted session.</summary>
        NoSession,

        /// <summary>A post from another origin.</summary>
        CrossOrigin,

        /// <summary>The lookup budget is spent.</summary>
        Limited,
    }

    /// <summary>The gate's decision, the live session and how long to wait.</summary>
    public sealed record Result(Outcome Outcome, (SessionRecord Session, UserRecord User) Live = default, TimeSpan Wait = default);

    /// <inheritdoc />
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

/// <summary>Renders the device page's answer to an approval (signed in) or a denial (forbidden) instead of OpenIddict's empty response.</summary>
public sealed class RenderDeviceDecision : IOpenIddictServerHandler<ApplyEndUserVerificationResponseContext>
{
    /// <inheritdoc />
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
            Errors.AccessDenied => new SignInPageModel(SignInStep.DeviceDenied, OidcIssuer.Slug),
            _ => new SignInPageModel(SignInStep.DeviceEnter, OidcIssuer.Slug, Error: context.Response.ErrorDescription ?? "Start again from your terminal.")
            {
                ErrorTitle = "The device could not be approved",
            },
        };
        await SignInPageModel.Render(http, page, error is null or Errors.AccessDenied ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest)
            .ExecuteAsync(http).ConfigureAwait(false);
        context.HandleRequest();
    }
}

/// <summary>Adds RFC 8628's <c>interval</c> to device authorization responses.</summary>
public sealed class AnnouncePollInterval : IOpenIddictServerHandler<ApplyDeviceAuthorizationResponseContext>
{
    /// <inheritdoc />
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

/// <summary>
/// Answers <c>slow_down</c> (RFC 8628 §3.5) to a device-code poll sooner than <see cref="DeviceFlow.Interval"/> after the
/// previous one of the same device code. Unknown codes fall through to OpenIddict's own <c>invalid_grant</c>.
/// </summary>
public sealed class PollNoFasterThanInterval(IOpenIddictTokenManager tokens, AttemptLimiter limiter) : IOpenIddictServerHandler<ProcessAuthenticationContext>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(ProcessAuthenticationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EndpointType is not OpenIddictServerEndpointType.Token || !context.Request.IsDeviceCodeGrantType()
            || context.Request.DeviceCode is not { Length: > 0 and <= 512 } code
            || await tokens.FindByReferenceIdAsync(code, context.CancellationToken).ConfigureAwait(false) is not { } token
            || await tokens.GetIdAsync(token, context.CancellationToken).ConfigureAwait(false) is not { } id)
        {
            return;
        }

        if (await limiter.AcquireCooldownAsync(OidcIssuer.InstanceId, "device-poll", id, DeviceFlow.Interval, context.CancellationToken).ConfigureAwait(false) is not null)
        {
            context.Reject("slow_down", $"Poll at most every {DeviceFlow.Interval.TotalSeconds:0} seconds, and 5 seconds slower after each slow_down.");
        }
    }
}

/// <summary><c>POST /{slug}/oauth/device</c>: the device authorization endpoint (RFC 8628), served by OpenIddict.</summary>
public sealed class DeviceAuthorizationEndpoint : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/{slug}/oauth/device");
        Name("oauth_device_authorization");
        Summary("Device authorization endpoint");
        Description("RFC 8628 for the public ts CLI client (client_id 00000000000000000000000007, display name ts-cli), application/x-www-form-urlencoded client_id and optional scope (openid email profile offline_access). Answers device_code, user_code, verification_uri ({public origin}/ui/terrascale/device), verification_uri_complete (adds ?user_code=), expires_in (600) and interval (5). The user signs in and approves or denies on that page; the CLI polls the token endpoint with grant_type=urn:ietf:params:oauth:grant-type:device_code and gets authorization_pending, slow_down (polled sooner than interval), access_denied or expired_token as RFC 8628 §3.5 says. Calls share the token endpoint's per-IP minute budget; exhaustion answers 429 rate_limited with Retry-After.");
        Tags("OAuth");
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<Dictionary<string, object>>();
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Results.NotFound());
}

/// <summary><c>GET /ui/{slug}/device</c>: the hosted device page; shows the entered code and the account, never approves.</summary>
public sealed class DevicePageEndpoint(TotpCredentials mfa, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
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

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DeviceFlow.PageAsync(this.HttpContext, mfa, options, cancellationToken);
}

/// <summary><c>POST /ui/{slug}/device</c>: approves or denies a device code for the signed-in user.</summary>
public sealed class DeviceDecisionEndpoint(TotpCredentials mfa, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/device");
        Name("ui_device_decide");
        Summary("Approve or deny a device");
        Description("Same-origin form post with user_code and decision=approve or deny, from a browser with a live session (otherwise 403). Approve signs the device in as the session's user with the scopes it asked for; an active MFA user's session must have proved the second factor (otherwise 403). Deny makes the device's next poll answer access_denied. A code works once; an unknown, expired or decided code answers 400. Lookups share the device page's limits (429 with Retry-After).");
        Tags("Sign-in");
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status400BadRequest, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DeviceFlow.PageAsync(this.HttpContext, mfa, options, cancellationToken);
}
