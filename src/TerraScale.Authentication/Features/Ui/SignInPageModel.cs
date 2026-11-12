using System.Text;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
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

    /// <summary>Ask before ending the session for a logout request without a matching ID token hint.</summary>
    SignOutConfirm,

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

    MfaEnroll,
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

    public string? Refresh { get; init; }

    public string Locale { get; init; } = AuthEmails.English;

    public string? UiLocales { get; init; }

    public HostedPageCopy Copy => HostedCopies.For(this.Locale);

    public string Sub(string format) => format.Replace("{email}", System.Net.WebUtility.HtmlEncode(this.Email ?? string.Empty));

    public string SubBrand(string format) => format.Replace("{brand}", System.Net.WebUtility.HtmlEncode(this.Brand));

    /// <summary>The correlation ID shared with the response's X-Request-ID header.</summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>The public origin used to classify footer destinations.</summary>
    public Uri? PublicOrigin { get; init; }

    /// <summary>A short bold headline over <see cref="Error"/>.</summary>
    public string? ErrorTitle { get; init; }

    /// <summary>Whether the error is a rate limit (a warning, not a failure).</summary>
    public bool Limited { get; init; }

    /// <summary>Whether this hour's code budget is spent; the page says so instead of implying a code was sent.</summary>
    public bool CodeBudgetExhausted { get; init; }

    /// <summary>Whole seconds until the disabled resend control re-enables when <see cref="CodeBudgetExhausted"/>.</summary>
    public long? WaitSeconds { get; init; }

    public string? ErrorId => this.Error is null ? null : "sign-in-error";

    /// <summary>The pending sign-up's token, carried from the sign-up form to its code form.</summary>
    public string? PendingToken { get; init; }

    /// <summary>The device's user code, as entered or opened from the CLI's link.</summary>
    public string? UserCode { get; init; }

    /// <summary>The signed-in account's email, shown before approving a device.</summary>
    public string? Account { get; init; }

    /// <summary>The registered client's display name, shown before approving a device.</summary>
    public string ClientName { get; init; } = "TerraScale CLI";

    /// <summary>Readable labels of the scopes the device asked for.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>The client's validated post-logout redirect URI the signed-out page continues to.</summary>
    public string? ReturnTo { get; init; }

    /// <summary>The end-session parameters the sign-out confirmation posts back (<see cref="SignInStep.SignOutConfirm"/>).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> LogoutFields { get; init; } = [];

    /// <summary>The same-origin page that starts the single sign-on (<see cref="SignInStep.Sso"/>).</summary>
    public string? SsoUrl { get; init; }

    public string? AccountUrl => this.Next is { } next && (next == AuthOptions.DashboardOrigin + "/profile"
        || next == AuthOptions.DashboardOrigin + "/profile?tab=security")
        ? next
        : null;

    /// <summary>The sealed second-factor challenge the second sign-in step posts back.</summary>
    public string? Challenge { get; init; }

    public bool AllowTotp { get; init; } = true;

    public bool AllowPasskey { get; init; } = true;

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

    /// <summary>The account's enrolled passkeys.</summary>
    public IReadOnlyList<PasskeyCredentialEntry> Passkeys { get; init; } = [];

    public IReadOnlyList<LinkedProvider> LinkedProviders { get; init; } = [];

    public IReadOnlyList<string> ConnectableProviders { get; init; } = [];

    /// <summary>A success message shown above the page.</summary>
    public string? Notice { get; init; }

    /// <summary>The page (tab) title.</summary>
    public string Title => this.Step switch
    {
        SignInStep.Password => this.Copy.TitlePassword,
        SignInStep.Code => this.Copy.TitleCode,
        SignInStep.SignUp => this.Copy.TitleSignUp,
        SignInStep.SignUpCode => this.Copy.TitleSignUpCode,
        SignInStep.Reset => this.Copy.TitleReset,
        SignInStep.ResetCode => this.Copy.TitleResetCode,
        SignInStep.ResetDone => this.Copy.TitleResetDone,
        SignInStep.SignedOut => this.Copy.TitleSignedOut,
        SignInStep.SignOutConfirm => this.Copy.TitleSignOutConfirm,
        SignInStep.Rejected => this.Copy.TitleRejected,
        SignInStep.Continue or SignInStep.SignedIn => this.Copy.TitleSignedIn,
        SignInStep.DeviceEnter => this.Copy.TitleDeviceEnter,
        SignInStep.DeviceConfirm => this.Copy.TitleDeviceConfirm,
        SignInStep.DeviceApproved => this.Copy.TitleDeviceApproved,
        SignInStep.DeviceDenied => this.Copy.TitleDeviceDenied,
        SignInStep.NotFound => this.Copy.TitleNotFound,
        SignInStep.Sso => this.Copy.TitleSso,
        SignInStep.Mfa => this.Copy.TitleMfa,
        SignInStep.MfaEnroll => this.Copy.TitleSecurity,
        SignInStep.Security => this.Copy.TitleSecurity,
        SignInStep.TotpSetup => this.Copy.TitleTotpSetup,
        SignInStep.RecoveryCodes => this.Copy.TitleRecoveryCodes,
        SignInStep.Reauthenticate => this.Copy.TitleReauthenticate,
        _ => this.Copy.TitleEmail,
    };

    /// <summary>The tenant's brand and sign-in methods.</summary>
    public HostedTheme Theme { get; init; } = HostedTheme.Platform;

    /// <summary>The brand name the page shows.</summary>
    public string Brand => this.Theme.Name ?? HostedLayoutModel.DefaultName;

    public string DefaultDestination => Tenancy.IsProject(this.Slug)
        ? SignInFlow.SecurityPath(this.Slug)
        : AuthOptions.DashboardOrigin;

    public string Link(string path, bool form = false)
    {
        List<string> query = [];
        if (!form && this.Next is { } next)
        {
            query.Add("next=" + Uri.EscapeDataString(next));
        }

        if (this.UiLocales is { } uiLocales)
        {
            query.Add("ui_locales=" + Uri.EscapeDataString(uiLocales));
        }

        return $"/ui/{this.Slug}/{path}" + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query));
    }

    internal static string? NavigationTarget(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 4096)
        {
            return null;
        }

        var encoded = new StringBuilder(url.Length);
        foreach (var rune in url.EnumerateRunes())
        {
            encoded.Append(rune.Value is > 32 and < 127 ? rune.ToString() : Uri.EscapeDataString(rune.ToString()));
        }

        var target = encoded.ToString();
        if (!Uri.TryCreate(target, UriKind.RelativeOrAbsolute, out var parsed))
        {
            return null;
        }

        target = parsed.IsAbsoluteUri ? parsed.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped) : target;
        return target.Length <= 4096 ? target : null;
    }

    private static string? ContinueTo(SignInPageModel model) => model.Step switch
    {
        SignInStep.Continue => model.Next,
        SignInStep.Sso => model.SsoUrl,
        SignInStep.SignedOut => model.ReturnTo,
        SignInStep.ResetDone => NavigationTarget(model.Link("sign-in")) ?? model.Link("sign-in", form: true),
        _ => null,
    };

    /// <summary>Renders the page with the hosted-page security headers and a fresh nonce.</summary>
    public static IResult Render(HttpContext context, SignInPageModel model, int statusCode = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);
        var nonce = BrowserSecurity.NewNonce();
        BrowserSecurity.ApplyPageHeaders(context.Response, nonce);
        var located = model with { Nonce = nonce, Theme = HostedTheme.Of(context), RequestId = context.TraceIdentifier, PublicOrigin = context.RequestServices.GetRequiredService<AuthOptions>().PublicOrigin, Locale = AuthEmails.Locale(context.Request), UiLocales = AuthEmails.UiLocales(context.Request) };
        var rendered = located with { Refresh = NavigationTarget(ContinueTo(located)) };
        var page = SignInPage.Create(rendered);
        page.StatusCode = statusCode;
        return page;
    }
}
