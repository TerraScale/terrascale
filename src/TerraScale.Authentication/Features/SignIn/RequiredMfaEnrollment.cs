using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Sso;

namespace TerraScale.Authentication.Features.SignIn;

internal static class RequiredMfaEnrollment
{
    internal const string Header = "X-TerraScale-Mfa-Challenge";

    internal static async Task<ChallengedSignIn> ReadAsync(HttpContext context, string? challenge, string factor, CancellationToken cancellationToken)
    {
        var tenant = SignInFlow.Tenant(context);
        var policy = (await context.RequestServices.GetRequiredService<ProjectAuthSettings>().GetAsync(tenant, cancellationToken)).Configuration ?? ConfigurationBody.Default;
        var project = Tenancy.IsProject(tenant);
        if (project && !policy.RequireMfa || !project && !context.RequestServices.GetRequiredService<SsoSignIn>().HasProof(context)
            || factor == "totp" && !policy.AllowTotp || factor == "passkey" && !policy.AllowPasskey
            || await context.RequestServices.GetRequiredService<TotpCredentials>().ChallengedAsync(tenant, challenge, "enrollment", cancellationToken) is not { } pending
            || !Tenancy.IsProject(tenant)
                && context.RequestServices.GetRequiredService<SsoSignIn>().FactorProofFor(context, pending.User) != pending.SsoProof
            || !(policy.RequireMfa || !Tenancy.IsProject(tenant)
                && await context.RequestServices.GetRequiredService<SsoSignIn>().RequiresMfaAsync(context, pending.User, cancellationToken))
            || !pending.User.InboxProven
            || await context.RequestServices.GetRequiredService<TotpCredentials>().HasAllowedFactorAsync(pending.User, pending.SsoProof is not null, cancellationToken))
        {
            throw new ApiException(403, "mfa_required", "Start sign-in again and enroll an allowed factor.");
        }

        return pending;
    }
}
