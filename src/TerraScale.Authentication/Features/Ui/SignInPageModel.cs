using System.Globalization;
using System.Text;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;

namespace TerraScale.Authentication.Features.Ui;

public enum SignInStep
{
    Email,

    Password,

    Code,

    Continue,

    SignedIn,

    SignedOut,

    SignOutConfirm,

    Rejected,

    SignUp,

    SignUpCode,

    Reset,

    ResetCode,

    ResetDone,

    DeviceEnter,

    DeviceConfirm,

    DeviceApproved,

    DeviceDenied,

    NotFound,

    Sso,

    SsoLink,

    Mfa,

    Security,

    TotpSetup,

    RecoveryCodes,

    Reauthenticate,

    MfaEnroll,

    EnrollmentInbox,
}

public sealed record SignInPageModel(SignInStep Step, string Slug = "", string? Email = null, string? Next = null, string? Error = null, string? Detail = null)
{
    public string Nonce { get; init; } = string.Empty;

    public string? Refresh { get; init; }

    public string Locale { get; init; } = AuthEmails.English;

    public string? UiLocales { get; init; }

    public HostedPageCopy Copy => HostedCopies.For(this.Locale);

    public string Sub(string format) => format.Replace("{email}", System.Net.WebUtility.HtmlEncode(this.Email ?? string.Empty));

    public string SubBrand(string format) => format.Replace("{brand}", System.Net.WebUtility.HtmlEncode(this.Brand));

    public string RequestId { get; init; } = string.Empty;

    public Uri? PublicOrigin { get; init; }

    public string? ErrorTitle { get; init; }

    public bool Limited { get; init; }

    public bool CodeBudgetExhausted { get; init; }

    public long? WaitSeconds { get; init; }

    public string? CodeRequest { get; init; }

    public string? LinkProvider { get; init; }

    public string? Username { get; init; }

    public string? ErrorId => this.Error is null ? null : "sign-in-error";

    public string? PendingToken { get; init; }

    public string? UserCode { get; init; }

    public string? Account { get; init; }
    public string? ProfileName { get; init; }
    public string? ProfileImage { get; init; }

    public string ClientName { get; init; } = "TerraScale CLI";

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public string? ReturnTo { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> LogoutFields { get; init; } = [];

    public string? SsoUrl { get; init; }

    public string? SsoOrganization { get; init; }

    public string? SsoIssuer { get; init; }

    public string? SsoConfirmation { get; init; }

    public string? AccountUrl => this.Next is { } next && (next == AuthOptions.DashboardOrigin + "/profile"
        || next == AuthOptions.DashboardOrigin + "/profile?tab=security")
        ? next
        : null;

    public string? Challenge { get; init; }

    public bool AllowTotp { get; init; } = true;

    public bool AllowPasskey { get; init; } = true;

    public string? QrCode { get; init; }

    public string? Secret { get; init; }

    public IReadOnlyList<string> RecoveryCodes { get; init; } = [];

    public bool MfaEnabled { get; init; }

    public int RecoveryRemaining { get; init; }

    public IReadOnlyList<PasskeyCredentialEntry> Passkeys { get; init; } = [];

    public IReadOnlyList<LinkedProvider> LinkedProviders { get; init; } = [];

    public IReadOnlyList<string> ConnectableProviders { get; init; } = [];

    public string? Notice { get; init; }

    public string Title => this.Step switch
    {
        SignInStep.Password => this.Copy.TitlePassword,
        SignInStep.EnrollmentInbox => this.Copy.TitleCode,
        SignInStep.Code => this.LinkProvider is { } provider ? string.Format(CultureInfo.InvariantCulture, this.Copy.TitleSocialLink, provider) : this.Copy.TitleCode,
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
        SignInStep.SsoLink => this.Copy.TitleSsoLink,
        SignInStep.Mfa => this.Copy.TitleMfa,
        SignInStep.MfaEnroll => this.Copy.TitleSecurity,
        SignInStep.Security => this.Copy.TitleSecurity,
        SignInStep.TotpSetup => this.Copy.TitleTotpSetup,
        SignInStep.RecoveryCodes => this.Copy.TitleRecoveryCodes,
        SignInStep.Reauthenticate => this.Copy.TitleReauthenticate,
        _ => this.Copy.TitleEmail,
    };

    public HostedTheme Theme { get; init; } = HostedTheme.Platform;

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

    public static IResult Render(HttpContext context, SignInPageModel model, int statusCode = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);
        var nonce = BrowserSecurity.NewNonce();
        BrowserSecurity.ApplyPageHeaders(context.Response, nonce);
        var located = model with { Nonce = nonce, Theme = HostedTheme.Of(context), RequestId = context.TraceIdentifier, PublicOrigin = context.RequestServices.GetRequiredService<AuthOptions>().PublicOrigin, Locale = AuthEmails.Locale(context.Request), UiLocales = AuthEmails.UiLocales(context.Request) };
        var pending = located.Step == SignInStep.Code
            ? PendingSocialLinks.Open(context.RequestServices.GetRequiredService<AuthOptions>(), located.Slug,
                PendingSocialLinks.Read(context, located.Slug), context.RequestServices.GetRequiredService<TimeProvider>()) : null;
        pending = pending?.Email == located.Email ? pending : null;
        var rendered = located with
        {
            Refresh = NavigationTarget(ContinueTo(located)),
            CodeRequest = located.CodeRequest ?? (pending is not null ? "sign-in" : null),
            LinkProvider = located.LinkProvider ?? (pending is not null ? SocialProviders.Label(pending.Identity.Provider) : null),
        };
        var page = SignInPage.Create(rendered);
        page.StatusCode = statusCode;
        return page;
    }
}
