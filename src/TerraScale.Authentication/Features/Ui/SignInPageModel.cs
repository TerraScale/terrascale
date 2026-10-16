using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Authentication.Features.Oidc;

namespace TerraScale.Authentication.Features.Ui;

/// <summary>What the hosted sign-in page shows.</summary>
public enum SignInStep
{
    /// <summary>Ask for the email address.</summary>
    Email,

    /// <summary>Ask for the emailed code.</summary>
    Code,

    /// <summary>Signed in; continue to the authorization request.</summary>
    Continue,

    /// <summary>Signed in with nowhere to return to.</summary>
    SignedIn,

    /// <summary>Signed out.</summary>
    SignedOut,

    /// <summary>A cross-origin form post, or an authorization or end-session request, was refused.</summary>
    Rejected,
}

/// <summary>Model for <c>SignInPage.cshtml</c>. Razor encodes every value.</summary>
/// <param name="Step">What to show.</param>
/// <param name="Slug">The issuer slug the forms post to.</param>
/// <param name="Email">The address entered so far.</param>
/// <param name="Next">A validated same-origin authorization URL to resume.</param>
/// <param name="Error">A message about the last attempt.</param>
/// <param name="Detail">A machine-readable error code shown under a refused request, e.g. <c>invalid_request</c>.</param>
public sealed record SignInPageModel(SignInStep Step, string Slug = "", string? Email = null, string? Next = null, string? Error = null, string? Detail = null)
{
    /// <summary>The response's CSP nonce, set by <see cref="Render"/>.</summary>
    public string Nonce { get; init; } = string.Empty;

    /// <summary>The attempt was rate limited; <see cref="Error"/> says when to retry.</summary>
    public bool Limited { get; init; }

    /// <summary>
    /// The state tile above the title (a Lucide icon and its tone: <c>success</c>, <c>warning</c>, <c>danger</c> or
    /// <c>accent</c>), or <see langword="null"/> on the plain email and code steps.
    /// </summary>
    public (string Icon, string Tone)? Tile => this.Step switch
    {
        SignInStep.SignedIn => (HostedIcons.CircleCheck, "success"),
        SignInStep.SignedOut => (HostedIcons.LogOut, "success"),
        SignInStep.Continue => (HostedIcons.ArrowRight, "accent"),
        SignInStep.Rejected => (HostedIcons.ShieldAlert, "danger"),
        _ when this.Limited => (HostedIcons.Timer, "warning"),
        _ => null,
    };

    /// <summary>The page title.</summary>
    public string Title => this.Step switch
    {
        SignInStep.Code => "Check your email",
        SignInStep.SignedOut => "Signed out",
        SignInStep.Rejected => "Request refused",
        SignInStep.Continue or SignInStep.SignedIn => "Signed in",
        _ => "Sign in",
    };

    /// <summary>The application being signed in to, when the authorization request names a known client.</summary>
    public string? App =>
        this.Next is { } next && next.IndexOf('?', StringComparison.Ordinal) is var start and >= 0
        && QueryHelpers.ParseQuery(next[start..]).TryGetValue("client_id", out var client) && client == OidcIssuer.DashboardClientId
            ? "TerraScale Dashboard"
            : null;

    /// <summary>Renders the page with the hosted-page security headers and a fresh nonce.</summary>
    public static IResult Render(HttpContext context, SignInPageModel model, int statusCode = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);
        var nonce = BrowserSecurity.NewNonce();
        BrowserSecurity.ApplyPageHeaders(context.Response, nonce);
        var page = SignInPage.Create(model with { Nonce = nonce });
        page.StatusCode = statusCode;
        return page;
    }
}
