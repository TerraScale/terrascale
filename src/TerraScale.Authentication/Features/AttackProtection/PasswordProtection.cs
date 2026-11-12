using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.AttackProtection;

public sealed class PasswordProtection(ProjectAuthSettings projects, AttackProtectionHttp http)
{
    public async Task<PasswordProblem> CheckAsync(string tenant, string? password, string email, CancellationToken cancellationToken)
    {
        if (PasswordPolicy.Check(password, email) is not PasswordProblem.None and var problem)
        {
            return problem;
        }

        return Tenancy.IsProject(tenant) && (await projects.GetAsync(tenant, cancellationToken)).AttackProtection.BreachedPasswords
            && await http.BreachedAsync(password!, cancellationToken)
            ? PasswordProblem.Breached
            : PasswordProblem.None;
    }
}
