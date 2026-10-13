using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Platform;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService : IPlatformCatalog
{
    // The platform organization's creator, which owns no membership: nobody can act in the organization as a member.
    private const string PlatformCreator = PlatformDatabases.OrganizationId;
    private const string PlatformOrganizationName = "TerraScale";

    public async ValueTask<EnsurePlatformResult> EnsurePlatformAsync(CancellationToken cancellationToken)
    {
        var tenants = await WriteAsync<EnsurePlatformResult>(async transaction =>
        {
            var organization = await GetAsync(transaction, Organizations, PlatformDatabases.OrganizationId, TerraBaseJsonContext.Default.Organization, cancellationToken)
                .ConfigureAwait(false);
            if (organization is null)
            {
                // Fixed timestamps too, so every seeding node writes identical records.
                organization = new Organization(PlatformDatabases.OrganizationId, PlatformOrganizationName, PlatformCreator, DateTimeOffset.UnixEpoch, PlanTier.Enterprise);
                await TouchOrganizationAsync(transaction, organization.Id, cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, OrganizationNames(PlatformCreator), Key(organization.Name), organization.Id, Id, cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, Organizations, organization.Id, organization, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);
            }
            else if (organization.CreatedBy != PlatformCreator || organization.Plan != PlanTier.Enterprise)
            {
                return new Conflict("the platform organization id holds another organization", "platform_organization_taken");
            }

            foreach (var (name, database) in PlatformDatabases.Databases)
            {
                var owner = await GetAsync(transaction, ProjectOrganizations, database.ProjectId, Id, cancellationToken).ConfigureAwait(false);
                if (owner is null)
                {
                    var project = new Project(database.ProjectId, organization.Id, name, DateTimeOffset.UnixEpoch);
                    await PutAsync(transaction, ProjectOrganizations, project.Id, organization.Id, Id, cancellationToken).ConfigureAwait(false);
                    await TouchOrganizationAsync(transaction, organization.Id, cancellationToken).ConfigureAwait(false);
                    await PutAsync(transaction, ProjectNames(organization.Id), Key(project.Name), project.Id, Id, cancellationToken).ConfigureAwait(false);
                    await PutAsync(transaction, Projects(organization.Id), project.Id, project, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false);
                }
                else if (owner != organization.Id)
                {
                    return new Conflict("a platform project id belongs to another organization", "platform_project_taken");
                }

                var binding = await GetAsync(transaction, Bindings(database.ProjectId), "ts_auth", TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken)
                    .ConfigureAwait(false);
                if (binding is null)
                {
                    await PutAsync(transaction, Bindings(database.ProjectId), "ts_auth",
                        new ProjectCatalogBinding(1, TerraBaseOptions.PlatformAuthIssuer, organization.Id, organization.Id, PlatformCreator, DateTimeOffset.UnixEpoch),
                        TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken).ConfigureAwait(false);
                }
                else if (binding.Version != 1 || binding.Issuer != TerraBaseOptions.PlatformAuthIssuer
                    || binding.OrganizationId != organization.Id || binding.AuthorityOrganizationId != organization.Id)
                {
                    return new Conflict("a platform project holds another authority binding", "platform_binding_taken");
                }
            }

            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (tenants is not Done)
        {
            return tenants;
        }

        var placement = PlatformDatabases.Placement(options.Region);
        foreach (var (name, database) in PlatformDatabases.Databases)
        {
            var added = await AddDatabaseAsync(
                async transaction => await OrganizationProjectAsync(transaction, PlatformDatabases.OrganizationId, database.ProjectId, cancellationToken)
                    .ConfigureAwait(false) is { } tenant ? tenant : ManagementErrors.NotFound,
                name, sourceId: null, branchedFrom: null, cancellationToken, id: database.Id, placement: placement, createdAt: DateTimeOffset.UnixEpoch).ConfigureAwait(false);
            EnsurePlatformResult result = added switch
            {
                AddedDatabase => new Done(),
                // Taken: by this seed on an earlier start or a concurrent run, which is fine, or by something else.
                Conflict error when error == ManagementErrors.NameTaken =>
                    await FindDatabaseRecordAsync(PlatformDatabases.OrganizationId, database.ProjectId, name, cancellationToken).ConfigureAwait(false) switch
                    {
                        ManagedDatabase existing when existing.Id == database.Id => new Done(),
                        ManagedDatabase => new Conflict("a platform database name holds another database", "platform_database_taken"),
                        NotFound => new Unavailable("a platform database was deleted while it was seeded"),
                        Unavailable unreadable => unreadable,
                    },
                Conflict error => error,
                Unavailable error => error,
                NotFound or Forbidden or QuotaExceeded => new Unavailable("the platform project is not ready"),
            };
            if (result is not Done)
            {
                return result;
            }
        }

        return new Done();
    }
}
