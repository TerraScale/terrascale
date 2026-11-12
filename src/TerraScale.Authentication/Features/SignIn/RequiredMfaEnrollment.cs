using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.SignIn;

internal static class RequiredMfaEnrollment
{
    internal const string Header = "X-TerraScale-Mfa-Challenge";

    internal static async Task<ChallengedSignIn> ReadAsync(HttpContext context, string? challenge, string factor, CancellationToken cancellationToken)
    {
        var tenant = SignInFlow.Tenant(context);
        var policy = (await context.RequestServices.GetRequiredService<ProjectAuthSettings>().GetAsync(tenant, cancellationToken)).Configuration ?? ConfigurationBody.Default;
        if (!Tenancy.IsProject(tenant) || !policy.RequireMfa || factor == "totp" && !policy.AllowTotp || factor == "passkey" && !policy.AllowPasskey
            || await context.RequestServices.GetRequiredService<TotpCredentials>().ChallengedAsync(tenant, challenge, cancellationToken) is not { } pending)
        {
            throw new ApiException(403, "mfa_required", "Start sign-in again and enroll an allowed factor.");
        }

        return pending;
    }
}
