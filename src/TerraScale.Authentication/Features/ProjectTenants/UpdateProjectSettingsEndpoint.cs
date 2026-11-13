using System.Text.RegularExpressions;
using TerraScale.Authentication.Features.AttackProtection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>PUT /v1/{project}/settings</c>.</summary>
public sealed partial class UpdateProjectSettingsEndpoint(TenantDirectory tenants, ProjectAuthSettings settings, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(Root + "/settings");
        this.Describe("update_project_settings", "Change a project tenant's settings", "Omitted methods are unchanged and a sent branding replaces the branding (null clears a value). At least one of passcode and password stays on; logo_url and footer links are https, colours #rrggbb, name 1 to 64 characters, title_size 20 to 40, default_theme light/dark/system and email_message at most 500 plain-text characters (only newline controls). Name, logo and accent also brand verification, sign-in and reset emails; sender stays TerraScale. Configuration replaces the project redirect allowlist (at most 100 exact URLs; empty adds no restriction) and session lifetime (300 to 2592000 seconds). require_mfa requires an enrolled allowed factor before sign-in; allow_totp and allow_passkey select the allowed factors, with at least one required when require_mfa is true. inactivity_timeout_minutes is null (off) or 15 to 43200 minutes (30 days). single_session supersedes prior sessions on each sign-in. These policies cannot extend session_lifetime_seconds. Needs the admin project role.", "Project tenants", typeof(UpdateSettingsRequest), StatusCodes.Status200OK, typeof(SettingsBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(ProjectTenantJson.Default.UpdateSettingsRequest, cancellationToken);
        if (request.Branding is { } branding)
        {
            if (branding.Name is { } name && (PlatformDirectory.Name(name) != name || name.Length > 64)
                || branding.LogoUrl is { } logo && !Https(logo)
                || new[] { branding.ThemeColor, branding.BackgroundColor, branding.TextColor }.Any(color => color is not null && !Color().IsMatch(color))
                || branding.TitleSize is < 20 or > 40
                || branding.DefaultTheme is not (null or "light" or "dark" or "system")
                || branding.EmailMessage is { } message && (message.Length > 500 || message.Any(character => char.IsControl(character) && character != '\n'))
                || branding.FooterLinks is { } links && (links.Count > 5 || links.Any(link => link is null || string.IsNullOrWhiteSpace(link.Label)
                    || link.Label.Length > 24 || link.Label.Any(char.IsControl) || !Https(link.Url))))
            {
                throw Invalid("Invalid branding: use a name of 1 to 64 characters, https URLs, #rrggbb colours, title_size 20 to 40, light/dark/system, at most five labelled footer links and email_message of at most 500 plain-text characters without controls except newline.");
            }
        }

        if (request.Configuration is { } configuration
            && (configuration.SessionLifetimeSeconds is < 300 or > 2592000
                || configuration.InactivityTimeoutMinutes is < 15 or > 43200
                || configuration.RequireMfa && !configuration.AllowTotp && !configuration.AllowPasskey || configuration.RedirectUrls is null
                || configuration.RedirectUrls.Count > 100 || configuration.RedirectUrls.Any(value => !Redirect(value))))
        {
            throw Invalid("configuration requires at least one allowed factor when MFA is required, inactivity_timeout_minutes null or 15 to 43200, session_lifetime_seconds 300 to 2592000 and at most 100 exact https redirect URLs (http only on loopback), without userinfo or fragments.");
        }

        if (request.AttackProtection is { BotChallenge: true } && !this.HttpContext.RequestServices.GetRequiredService<TurnstileKeys>().Configured)
        {
            throw Invalid("Bot challenge requires configured Cloudflare Turnstile site and secret keys.");
        }

        if (request.Methods?.SignUpMode is not (null or "public" or "restricted" or "waitlist"))
        {
            throw Invalid("sign_up_mode must be public, restricted or waitlist.");
        }

        var restrictions = request.Restrictions is { } sentRestrictions ? SignUpRestrictions.Normalize(sentRestrictions) : null;
        var methods = request.Methods;
        var updated = await settings.UpdateAsync(projectId, current =>
        {
            var next = current with { Passcode = methods?.Passcode ?? current.Passcode, Password = methods?.Password ?? current.Password, SignUpMode = methods?.SignUpMode ?? current.SignUpMode };
            next = request.Branding is { } sent ? next with
            {
                Name = sent.Name,
                LogoUrl = sent.LogoUrl,
                ThemeColor = sent.ThemeColor?.ToLowerInvariant(),
                TitleSize = sent.TitleSize,
                DefaultTheme = sent.DefaultTheme,
                FooterLinks = sent.FooterLinks,
                BackgroundColor = sent.BackgroundColor?.ToLowerInvariant(),
                TextColor = sent.TextColor?.ToLowerInvariant(),
                EmailMessage = sent.EmailMessage,
            } : next;
            next = request.Configuration is { } policy ? next with { Configuration = policy } : next;
            next = request.AttackProtection is { } protection ? next with { AttackProtection = protection } : next;
            next = restrictions is not null ? next with { Restrictions = restrictions } : next;
            return next.Passcode || next.Password ? next : throw Invalid("At least one of passcode and password must stay on.");
        }, cancellationToken);
        return Results.Json(await this.SettingsAsync(projectId, updated, cancellationToken), ProjectTenantJson.Default.SettingsBody);
    }

    private static bool Https(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && value.Length <= 2048;

    private static bool Redirect(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && value.Length <= 2048 && string.IsNullOrEmpty(uri.UserInfo) && !value.Contains('#', StringComparison.Ordinal)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    [GeneratedRegex("\\A#[0-9a-fA-F]{6}\\z")]
    private static partial Regex Color();
}
