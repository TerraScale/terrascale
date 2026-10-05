namespace TerraScale.Auth.Features.Ui;

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

    /// <summary>A cross-origin form post was refused.</summary>
    Rejected,
}

/// <summary>Model for <c>SignInPage.cshtml</c>. Razor encodes every value.</summary>
/// <param name="Step">What to show.</param>
/// <param name="Slug">The issuer slug the forms post to.</param>
/// <param name="Email">The address entered so far.</param>
/// <param name="Next">A validated same-origin authorization URL to resume.</param>
/// <param name="Error">A message about the last attempt.</param>
public sealed record SignInPageModel(SignInStep Step, string Slug = "", string? Email = null, string? Next = null, string? Error = null)
{
    /// <summary>The response's CSP nonce, set by <see cref="Render"/>.</summary>
    public string Nonce { get; init; } = string.Empty;

    /// <summary>The page title.</summary>
    public string Title => this.Step switch
    {
        SignInStep.Code => "Check your email",
        SignInStep.SignedOut => "Signed out",
        SignInStep.Rejected => "Request refused",
        SignInStep.Continue or SignInStep.SignedIn => "Signed in",
        _ => "Sign in",
    };

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
