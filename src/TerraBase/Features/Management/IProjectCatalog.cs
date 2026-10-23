using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>A verified project namespace, known absence, or an unreadable catalog.</summary>
public readonly union ResolveProjectResult(string, NotFound, Unavailable);

/// <summary>A verified legacy project binding, or why verification failed.</summary>
public readonly union VerifyProjectBindingResult(Done, NotFound, Conflict, Unavailable);

/// <summary>Resolves ts-auth project ids to the compatible on-disk catalog namespace.</summary>
public interface IProjectCatalog
{
    /// <summary>
    /// Finds a verified storage namespace, or registers a signed project's first database atomically under
    /// <paramref name="createIn"/>, the ts-auth organization the token names; null only resolves.
    /// </summary>
    ValueTask<ResolveProjectResult> ResolveProjectAsync(string projectId, string actor, string? createIn, CancellationToken cancellationToken);

    /// <summary>The organization that registered <paramref name="projectId"/>, whether or not ts-auth bound it yet.</summary>
    ValueTask<ResolveProjectResult> ProjectOrganizationAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>Sets the plan of the storage organization a signed project enrolled; legacy-bound projects keep their owners' plan.</summary>
    ValueTask<SetPlanResult> SetEnrolledPlanAsync(string projectId, PlanTier plan, CancellationToken cancellationToken);

    /// <summary>Verifies a legacy owner's sign-in token with ts-auth before binding existing storage to its project.</summary>
    ValueTask<VerifyProjectBindingResult> VerifyBindingAsync(
        string userId, string organizationId, string projectId, string authorityOrganizationId, string token, CancellationToken cancellationToken);
}
