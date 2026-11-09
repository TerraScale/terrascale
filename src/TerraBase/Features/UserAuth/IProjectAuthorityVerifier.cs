using TerraBase.Errors;

namespace TerraBase.Features.UserAuth;

public readonly union ProjectAuthorityResult(bool, Unavailable);

internal interface IProjectAuthorityVerifier
{
    ValueTask<ProjectAuthorityResult> IsRecoveryAdministratorAsync(string token, string userId, string organizationId, string projectId, CancellationToken cancellationToken);
}
