using TerraBase.Errors;

namespace TerraBase.Features.UserAuth;

/// <summary>Live project authority, or ts-auth is unavailable.</summary>
public readonly union ProjectAuthorityResult(bool, Unavailable);

/// <summary>Checks live ts-auth authority for storage binding and authenticated operator recovery.</summary>
public interface IProjectAuthorityVerifier
{
    /// <summary>Whether the sign-in token's user currently administers the exact project in ts-auth.</summary>
    ValueTask<ProjectAuthorityResult> IsProjectAdministratorAsync(string token, string organizationId, string projectId, CancellationToken cancellationToken);

    /// <summary>Whether the proof belongs to the acting user, an organization owner/admin of the exact project.</summary>
    ValueTask<ProjectAuthorityResult> IsRecoveryAdministratorAsync(string token, string userId, string organizationId, string projectId, CancellationToken cancellationToken);
}
