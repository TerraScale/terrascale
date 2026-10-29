using TerraBase.Errors;

namespace TerraBase.Features.UserAuth;

/// <summary>Live project authority, or ts-auth is unavailable.</summary>
public readonly union ProjectAuthorityResult(bool, Unavailable);

internal interface IProjectAuthorityVerifier
{
    /// <summary>Whether the proof belongs to the acting user, an organization owner/admin of the exact project.</summary>
    ValueTask<ProjectAuthorityResult> IsRecoveryAdministratorAsync(string token, string userId, string organizationId, string projectId, CancellationToken cancellationToken);
}
