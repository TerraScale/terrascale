using TerraBase.Errors;

namespace TerraBase.Features.Management;

/// <summary>A verified project namespace, known absence, or an unreadable catalog.</summary>
public readonly union ResolveProjectResult(string, NotFound, Unavailable);

/// <summary>A verified legacy project binding, or why verification failed.</summary>
public readonly union VerifyProjectBindingResult(Done, NotFound, Conflict, Unavailable);

/// <summary>Resolves ts-auth project ids to the compatible on-disk catalog namespace.</summary>
public interface IProjectCatalog
{
    /// <summary>Finds a verified storage namespace, or registers one atomically for a signed project's first database.</summary>
    ValueTask<ResolveProjectResult> ResolveProjectAsync(string projectId, string actor, bool create, CancellationToken cancellationToken);

    /// <summary>Verifies a legacy owner's sign-in token with ts-auth before binding existing storage to its project.</summary>
    ValueTask<VerifyProjectBindingResult> VerifyBindingAsync(
        string userId, string organizationId, string projectId, string authorityOrganizationId, string token, CancellationToken cancellationToken);
}
