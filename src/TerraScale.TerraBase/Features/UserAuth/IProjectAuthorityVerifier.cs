using TerraBase.Errors;

namespace TerraBase.Features.UserAuth;

/// <summary>Live project authority, or ts-auth is unavailable.</summary>
public readonly union ProjectAuthorityResult(bool, Unavailable);

/// <summary>Checks live ts-auth authority for the one-time migration of legacy project storage.</summary>
public interface IProjectAuthorityVerifier
{
    /// <summary>Whether the sign-in token's user currently administers the exact project in ts-auth.</summary>
    ValueTask<ProjectAuthorityResult> IsProjectAdministratorAsync(string token, string organizationId, string projectId, CancellationToken cancellationToken);
}
