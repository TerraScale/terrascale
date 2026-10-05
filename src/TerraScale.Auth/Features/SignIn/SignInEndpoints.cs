using TerraScale.Auth.Configuration;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.Ui;
using TerraScale.Auth.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Auth.Features.SignIn;

/// <summary>The fields the hosted sign-in forms post (<c>application/x-www-form-urlencoded</c>).</summary>
/// <param name="Email">The account's email address.</param>
/// <param name="Code">The six-digit emailed code (verify only).</param>
/// <param name="Next">The authorization request to resume, <c>/{slug}/oauth/authorize?…</c>.</param>
public sealed record SignInForm(string? Email, string? Code, string? Next);

/// <summary><c>GET /ui/{slug}/sign-in</c>: asks for the email address to send a sign-in code to.</summary>
public sealed class SignInPageEndpoint : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/ui/{slug}/sign-in");
        Name("ui_sign_in");
        Summary("Hosted sign-in page");
        Description("Email passcode sign-in for existing accounts. next is honoured only when it is this issuer's /{slug}/oauth/authorize URL.");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override Task<IResult> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(SignInFlow.WrongSlug(this.HttpContext)
            ? Results.NotFound()
            : SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Email, OidcIssuer.Slug, Next: SignInFlow.SafeNext(this.HttpContext.Request.Query["next"]))));
}

/// <summary>
/// <c>POST /ui/{slug}/sign-in</c>: emails a code when the address has an account. The page and its timing are the same
/// either way: <see cref="SignInMail"/> delivers after the response.
/// </summary>
public sealed class RequestPasscodeEndpoint(Passcodes passcodes, SignInMail mail, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in");
        Name("ui_sign_in_request_code");
        Summary("Email a sign-in code");
        Description("Same-origin form post (the Origin header must be the public origin). The response never reveals whether the address has an account. At most 5 codes per account per hour; a new code replaces the previous one.");
        Tags("Sign-in");
        Accepts<SignInForm>("application/x-www-form-urlencoded");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async Task<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var address = form.Email?.Trim() ?? string.Empty;
        if (await passcodes.RequestAsync(OidcIssuer.InstanceId, address, cancellationToken) is { } issued)
        {
            mail.Enqueue(issued);
        }

        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Code, OidcIssuer.Slug, address, SignInFlow.SafeNext(form.Next)));
    }
}

/// <summary>
/// <c>POST /ui/{slug}/sign-in/verify</c>: redeems the code, opens a fresh session and sets the session cookie, then
/// resumes the authorization request.
/// </summary>
public sealed class VerifyPasscodeEndpoint(Passcodes passcodes, Sessions sessions, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/verify");
        Name("ui_sign_in_verify_code");
        Summary("Sign in with an emailed code");
        Description("Same-origin form post. A code works once, for 10 minutes, and dies after 5 wrong guesses. Success sets the tsauth_session cookie (Secure, HttpOnly, SameSite=Lax) and continues to next.");
        Tags("Sign-in");
        Accepts<SignInForm>("application/x-www-form-urlencoded");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async Task<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var address = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(form.Next);
        if (await passcodes.VerifyAsync(OidcIssuer.InstanceId, address, form.Code ?? string.Empty, cancellationToken) is not { } session)
        {
            return SignInPageModel.Render(this.HttpContext,
                new SignInPageModel(SignInStep.Code, OidcIssuer.Slug, address, next, "That code is wrong or has expired. Request a new one if needed."), StatusCodes.Status401Unauthorized);
        }

        // The browser's previous session, if any, ends with this sign-in.
        await sessions.RevokeAsync(OidcIssuer.InstanceId, this.HttpContext.Request.Cookies[SecuritySchemes.SessionCookieName], cancellationToken);
        BrowserSecurity.SetSessionCookie(this.HttpContext.Response, session.Token);
        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(next is null ? SignInStep.SignedIn : SignInStep.Continue, OidcIssuer.Slug, Next: next));
    }
}

/// <summary>Checks shared by the sign-in forms.</summary>
internal static class SignInFlow
{
    public static bool WrongSlug(HttpContext context) => !string.Equals(context.GetRouteValue("slug") as string, OidcIssuer.Slug, StringComparison.Ordinal);

    /// <summary>Only this issuer's authorization endpoint may be resumed, as a same-origin path.</summary>
    public static string? SafeNext(string? next) =>
        next is not null && next.StartsWith($"/{OidcIssuer.Slug}/oauth/authorize?", StringComparison.Ordinal) && next.Length <= 4096
            && !next.Any(character => character is '\\' or '#' || char.IsControl(character) || char.IsWhiteSpace(character))
            ? next
            : null;

    /// <summary>
    /// The posted form, or <see langword="null"/> for a wrong slug, a cross-origin post or a body that is not a form.
    /// Login CSRF is refused by requiring the browser's Origin header to be the public origin.
    /// </summary>
    public static async Task<SignInForm?> ReadAsync(HttpContext context, AuthOptions options, CancellationToken cancellationToken)
    {
        if (WrongSlug(context) || !context.Request.HasFormContentType
            || context.Request.Headers.Origin is not [{ } origin] || !IsPublicOrigin(origin, options.PublicOrigin))
        {
            return null;
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        return new SignInForm(form["email"], form["code"], form["next"]);
    }

    /// <summary>
    /// Whether a serialized Origin (RFC 6454) names the public origin: same scheme, same host compared as the ASCII
    /// (punycode) form browsers send, and same effective port, so <c>:443</c> or letter case in the configuration
    /// never refuses a legitimate post. <c>null</c>, paths and credentials never match.
    /// </summary>
    public static bool IsPublicOrigin(string origin, Uri publicOrigin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
        && origin.Count(character => character == '/') == 2
        && string.IsNullOrEmpty(parsed.UserInfo) && string.IsNullOrEmpty(parsed.Query) && string.IsNullOrEmpty(parsed.Fragment)
        && parsed.Scheme == publicOrigin.Scheme
        && string.Equals(parsed.IdnHost, publicOrigin.IdnHost, StringComparison.OrdinalIgnoreCase)
        && parsed.Port == publicOrigin.Port;

    public static IResult Refused(HttpContext context) => WrongSlug(context)
        ? Results.NotFound()
        : SignInPageModel.Render(context, new SignInPageModel(SignInStep.Rejected, OidcIssuer.Slug), StatusCodes.Status403Forbidden);
}
