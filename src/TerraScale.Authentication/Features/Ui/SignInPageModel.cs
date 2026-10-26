using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;

namespace TerraScale.Authentication.Features.Ui;

/// <summary>What the hosted page shows.</summary>
public enum SignInStep
{
    /// <summary>Ask for the email address.</summary>
    Email,

    /// <summary>Ask for the account password.</summary>
    Password,

    /// <summary>Ask for the emailed code.</summary>
    Code,

    /// <summary>Signed in; continue to the authorization request.</summary>
    Continue,

    /// <summary>Signed in with nowhere to return to.</summary>
    SignedIn,

    /// <summary>Signed out, optionally continuing to the client's validated post-logout URI.</summary>
    SignedOut,

    /// <summary>A cross-origin form post, or an authorization or end-session request that cannot be answered to its client, was refused.</summary>
    Rejected,

    /// <summary>Ask for the email address and password of a new account.</summary>
    SignUp,

    /// <summary>Ask for the code that verifies a new account's email.</summary>
    SignUpCode,

    /// <summary>Ask for the email address of the account whose password to reset.</summary>
    Reset,

    /// <summary>Ask for the emailed reset code and the new password.</summary>
    ResetCode,

    /// <summary>The password was reset.</summary>
    ResetDone,

    /// <summary>Ask for the code a device (the ts CLI) shows.</summary>
    DeviceEnter,

    /// <summary>Show a device's code and the signed-in account, to approve or deny.</summary>
    DeviceConfirm,

    /// <summary>The device was approved.</summary>
    DeviceApproved,

    /// <summary>The device was denied.</summary>
    DeviceDenied,

    /// <summary>A browser opened a URL that does not exist.</summary>
    NotFound,

    /// <summary>The email's domain signs in through its organization's identity provider; continue there.</summary>
    Sso,

    /// <summary>Ask for the authenticator app's code or a recovery code after the primary credential.</summary>
    Mfa,

    /// <summary>The signed-in account's two-factor state.</summary>
    Security,

    /// <summary>Show a pending authenticator's QR code and ask for its first code.</summary>
    TotpSetup,

    /// <summary>Two-factor authentication was turned on; show the recovery codes once.</summary>
    RecoveryCodes,

    /// <summary>A credential change needs a recent sign-in.</summary>
    Reauthenticate,
}

/// <summary>Model for <c>SignInPage.cshtml</c>. Razor encodes every value.</summary>
/// <param name="Step">What to show.</param>
/// <param name="Slug">The issuer slug the forms post to.</param>
/// <param name="Email">The address entered so far.</param>
/// <param name="Next">A validated same-origin URL to resume: an authorization request or the device page.</param>
/// <param name="Error">A message about the last attempt.</param>
/// <param name="Detail">A machine-readable error code shown under a refused request, e.g. <c>invalid_request</c>.</param>
public sealed record SignInPageModel(SignInStep Step, string Slug = "", string? Email = null, string? Next = null, string? Error = null, string? Detail = null)
{
    /// <summary>The response's CSP nonce, set by <see cref="Render"/>.</summary>
    public string Nonce { get; init; } = string.Empty;

    /// <summary>A short bold headline over <see cref="Error"/>.</summary>
    public string? ErrorTitle { get; init; }

    /// <summary>Whether the error is a rate limit (a warning, not a failure).</summary>
    public bool Limited { get; init; }

    /// <summary>The server-derived deadline for retrying a refused request.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>The server's rounded remaining wait, independent of the browser's wall clock.</summary>
    public long? RetrySeconds { get; init; }

    /// <summary>A conservative resend deadline after a mail request; refusal always supplies the authoritative remaining wait.</summary>
    public DateTimeOffset? ResendAt { get; init; }

    /// <summary>The pending sign-up's token, carried from the sign-up form to its code form.</summary>
    public string? PendingToken { get; init; }

    /// <summary>The device's user code, as entered or opened from the CLI's link.</summary>
    public string? UserCode { get; init; }

    /// <summary>The registered application requesting device consent.</summary>
    public string? ClientName { get; init; }

    /// <summary>Descriptions of the validated scopes bound to the device code.</summary>
    public IReadOnlyList<string> RequestedPermissions { get; init; } = [];

    /// <summary>The signed-in account's email, shown before approving a device.</summary>
    public string? Account { get; init; }

    /// <summary>The client's validated post-logout redirect URI the signed-out page continues to.</summary>
    public string? ReturnTo { get; init; }

    /// <summary>The validated first-party account page, when this ceremony started there.</summary>
    public string? AccountReturn => HostedAccountReturn.Allowed(this.Slug, this.Next)
        ? HostedAccountReturn.AccountUrl : null;

    /// <summary>The same-origin page that starts the single sign-on (<see cref="SignInStep.Sso"/>).</summary>
    public string? SsoUrl { get; init; }

    /// <summary>The sealed second-factor challenge the second sign-in step posts back.</summary>
    public string? Challenge { get; init; }

    /// <summary>The pending authenticator's QR code, as a PNG data URI.</summary>
    public string? QrCode { get; init; }

    /// <summary>The pending authenticator's base32 key, for manual entry.</summary>
    public string? Secret { get; init; }

    /// <summary>Recovery codes, shown once at activation.</summary>
    public IReadOnlyList<string> RecoveryCodes { get; init; } = [];

    /// <summary>Whether the account has an active authenticator.</summary>
    public bool MfaEnabled { get; init; }

    /// <summary>Unused recovery codes left.</summary>
    public int RecoveryRemaining { get; init; }

    /// <summary>A success message shown above the page.</summary>
    public string? Notice { get; init; }

    /// <summary>The page (tab) title.</summary>
    public string Title => this.Step switch
    {
        SignInStep.Password => "Enter your password",
        SignInStep.Code => "Check your email",
        SignInStep.SignedOut => "Signed out",
        SignInStep.Rejected => "Request refused",
        SignInStep.Continue or SignInStep.SignedIn => "Signed in",
        SignInStep.SignUp => "Create your account",
        SignInStep.SignUpCode => "Verify your email",
        SignInStep.Reset => "Reset your password",
        SignInStep.ResetCode => "Choose a new password",
        SignInStep.ResetDone => "Password updated",
        SignInStep.DeviceEnter => "Connect a device",
        SignInStep.DeviceConfirm => "Approve this device?",
        SignInStep.DeviceApproved => "Device connected",
        SignInStep.DeviceDenied => "Request denied",
        SignInStep.NotFound => "Page not found",
        SignInStep.Sso => "Continue with single sign-on",
        SignInStep.Mfa => "Two-factor authentication",
        SignInStep.Security => "Account security",
        SignInStep.TotpSetup => "Set up your authenticator app",
        SignInStep.RecoveryCodes => "Save your recovery codes",
        SignInStep.Reauthenticate => "Sign in again to continue",
        _ => "Sign in",
    };

    /// <summary>The tenant's brand and sign-in methods.</summary>
    public HostedTheme Theme { get; init; } = HostedTheme.Platform;

    /// <summary>The brand name the page shows.</summary>
    public string Brand => this.Theme.Name ?? HostedLayoutModel.DefaultName;

    /// <summary>The step's heading.</summary>
    public string Heading => this.Step == SignInStep.Email ? $"Sign in to {this.Brand}" : this.Title;

    /// <summary><paramref name="path"/> under this issuer's hosted pages, carrying <see cref="Next"/>.</summary>
    public string Link(string path) => $"/ui/{this.Slug}/{path}" + (this.Next is null ? string.Empty : "?next=" + Uri.EscapeDataString(this.Next));

    /// <summary>Renders the page with the hosted-page security headers and a fresh nonce.</summary>
    public static IResult Render(HttpContext context, SignInPageModel model, int statusCode = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);
        if (model.Step is SignInStep.Security or SignInStep.TotpSetup or SignInStep.RecoveryCodes)
        {
            model = model with { Next = HostedAccountReturn.Next(context) };
        }
        var nonce = BrowserSecurity.NewNonce();
        BrowserSecurity.ApplyPageHeaders(context.Response, nonce);
        var page = SignInPage.Create(model with { Nonce = nonce, Theme = HostedTheme.Of(context) });
        page.StatusCode = statusCode;
        return page;
    }
}
