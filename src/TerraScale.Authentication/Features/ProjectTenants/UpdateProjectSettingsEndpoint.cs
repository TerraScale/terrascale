using System.Text.RegularExpressions;
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
        this.Document("update_project_settings", "Change a project tenant's settings", "Omitted methods are unchanged and a sent branding replaces the branding (null clears a value). At least one of passcode and password stays on; logo_url is https, theme_color #rrggbb, name 1 to 64 characters. Needs the admin project role.", "Project tenants", typeof(UpdateSettingsRequest), StatusCodes.Status200OK, typeof(SettingsBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(ProjectTenantJson.Default.UpdateSettingsRequest, cancellationToken);
        if (request.Branding is { } branding
            && (branding.Name is { } name && (PlatformDirectory.Name(name) != name || name.Length > 64)
                || branding.LogoUrl is { } logo && (!Uri.TryCreate(logo, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                || branding.ThemeColor is { } color && !Color().IsMatch(color)))
        {
            throw Invalid("name must be 1 to 64 characters, logo_url an https URL and theme_color #rrggbb.");
        }

        var methods = request.Methods;
        var updated = await settings.UpdateAsync(projectId, current =>
        {
            var next = current with { Passcode = methods?.Passcode ?? current.Passcode, Password = methods?.Password ?? current.Password, SignUp = methods?.SignUp ?? current.SignUp };
            next = request.Branding is { } sent ? next with { Name = sent.Name, LogoUrl = sent.LogoUrl, ThemeColor = sent.ThemeColor?.ToLowerInvariant() } : next;
            return next.Passcode || next.Password ? next : throw Invalid("At least one of passcode and password must stay on.");
        }, cancellationToken);
        return Results.Json(await this.SettingsAsync(projectId, updated, cancellationToken), ProjectTenantJson.Default.SettingsBody);
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Color();
}
