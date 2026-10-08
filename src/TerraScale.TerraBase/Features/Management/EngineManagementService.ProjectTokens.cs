using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

internal sealed record ProjectCatalogBinding(
    int Version, string Issuer, string OrganizationId, string? AuthorityOrganizationId, string VerifiedBy, DateTimeOffset VerifiedAt);

internal sealed partial class EngineManagementService : IProjectCatalog
{
    public async ValueTask<string?> ResolveProjectAsync(string projectId, string actor, bool create, CancellationToken cancellationToken)
    {
        if (options.AuthIssuer is not { } issuer)
        {
            return null;
        }

        var mapping = await ReadAsync(async transaction =>
        {
            var organization = await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false);
            return (Organization: organization, Verified: organization is not null
                && await IsProjectBoundAsync(transaction, projectId, organization, cancellationToken).ConfigureAwait(false));
        }, cancellationToken)
            .ConfigureAwait(false);
        if (mapping.Organization is not null || !create)
        {
            return mapping.Verified ? mapping.Organization : null;
        }

        // Keep layout 1 and catalog 5: these records are storage envelopes, not ts-auth identity or membership.
        return await WriteAsync<string?>(async transaction =>
        {
            if (await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return await IsProjectBoundAsync(transaction, projectId, existing, cancellationToken).ConfigureAwait(false) ? existing : null;
            }

            if (await transaction.GetVersionAsync(ProjectOrganizations, projectId, cancellationToken).ConfigureAwait(false) is not null)
            {
                return null;
            }

            // Legacy commits let users choose organization UUIDs; equality is not authority over a signed project.
            var namespaceId = NewId();
            if (await transaction.GetVersionAsync(Organizations, namespaceId, cancellationToken).ConfigureAwait(false) is not null)
            {
                return null;
            }

            var owner = new Organization(namespaceId, projectId, actor, time.GetUtcNow());
            var project = new Project(projectId, owner.Id, projectId, time.GetUtcNow());
            await PutAsync(transaction, Organizations, owner.Id, owner, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Projects(owner.Id), project.Id, project, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, ProjectOrganizations, project.Id, owner.Id, Id, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Bindings(project.Id), "ts_auth",
                new ProjectCatalogBinding(1, issuer, owner.Id, null, actor, time.GetUtcNow()), TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken)
                .ConfigureAwait(false);
            return owner.Id;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<VerifyProjectBindingResult> VerifyBindingAsync(
        string userId, string organizationId, string projectId, string authorityOrganizationId, string token, CancellationToken cancellationToken)
    {
        if (options.AuthIssuer is not { } issuer || !await ReadAsync(
            transaction => CanBindProjectAsync(transaction, userId, organizationId, projectId, cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            return new NotFound();
        }

        switch (await projectAuthority.IsProjectAdministratorAsync(token, authorityOrganizationId, projectId, cancellationToken).ConfigureAwait(false))
        {
            case Unavailable unavailable:
                return unavailable;
            case false:
                return new NotFound();
            case true:
                break;
        }

        // The online authority check holds no storage locks. Recheck storage ownership and mapping in the committing transaction.
        return await WriteAsync<VerifyProjectBindingResult>(async transaction =>
        {
            if (!await CanBindProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false))
            {
                return new NotFound();
            }

            if (await GetAsync(transaction, Bindings(projectId), "ts_auth", TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken)
                .ConfigureAwait(false) is { } existing)
            {
                return existing is { Version: 1 } && existing.Issuer == issuer && existing.OrganizationId == organizationId
                    && existing.AuthorityOrganizationId == authorityOrganizationId
                    ? new Done() : new Conflict("the project already has a different verified binding", "binding_conflict");
            }

            await PutAsync(transaction, Bindings(projectId), "ts_auth",
                new ProjectCatalogBinding(1, issuer, organizationId, authorityOrganizationId, userId, time.GetUtcNow()),
                TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "project_binding_verified", projectId, detail: null, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string Bindings(string projectId) => SystemCatalog.ProjectState(projectId) + "binding";

    private async ValueTask<bool> IsProjectBoundAsync(IStorageTransaction transaction, string projectId, string organizationId, CancellationToken cancellationToken) =>
        await GetAsync(transaction, Bindings(projectId), "ts_auth", TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken).ConfigureAwait(false)
            is { Version: 1 } binding && binding.Issuer == options.AuthIssuer && binding.OrganizationId == organizationId
        && await GetAsync(transaction, Projects(organizationId), projectId, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false)
            is { } project && project.OrganizationId == organizationId;

    private static async ValueTask<bool> CanBindProjectAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        (await AuthorizeAsync(transaction, userId, organizationId, OrganizationRole.Owner, cancellationToken).ConfigureAwait(false)).Error == ManagementError.None
        && await GetAsync(transaction, Projects(organizationId), projectId, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false) is not null
        && await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) == organizationId;

    private static bool ProjectActor(string userId, string organizationId) =>
        ProjectAccess.Current is { } access && access.Actor == userId && access.OrganizationId == organizationId;
}
