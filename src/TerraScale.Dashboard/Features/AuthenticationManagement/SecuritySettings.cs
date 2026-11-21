using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

public static partial class AuthenticationConsole
{
    private static async Task<Valid<TsAuthProjectConfiguration>> SecurityConfigurationAsync(ITsAuthProjects projects, SessionRecord session, string project, IFormCollection form, CancellationToken cancellationToken)
    {
        int? minutes = null;
        if (!string.IsNullOrWhiteSpace(form["inactivity_timeout_minutes"]))
        {
            if (!int.TryParse(form["inactivity_timeout_minutes"], out var parsed) || parsed is < 15 or > 43200)
            {
                return new Validation("Inactivity timeout must be 15 to 43200 minutes, or empty.");
            }

            minutes = parsed;
        }

        var current = (await projects.GetSettingsAsync(session, project, cancellationToken)).Configuration ?? new TsAuthProjectConfiguration([]);
        return current with
        {
            RequireMfa = form.ContainsKey("require_mfa"),
            AllowTotp = form.ContainsKey("allow_totp"),
            AllowPasskey = form.ContainsKey("allow_passkey"),
            InactivityTimeoutMinutes = minutes,
            SingleSession = form.ContainsKey("single_session"),
        };
    }
}
