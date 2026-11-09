using TerraBase.Errors;
using TerraBase.Features.Platform;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService : IPlatformCatalog
{
    private const string PlatformCreator = PlatformDatabases.OrganizationId;
    private const string PlatformOrganizationName = "TerraScale";

    public async ValueTask<EnsurePlatformResult> EnsurePlatformAsync(CancellationToken cancellationToken)
    {
        var tenants = await WriteAsync<EnsurePlatformResult>(async transaction =>
        {
            var organization = await CatalogTables.OrganizationAsync(transaction, PlatformDatabases.OrganizationId, cancellationToken).ConfigureAwait(false);
            if (organization is null)
            {
                organization = new Organization(PlatformDatabases.OrganizationId, PlatformOrganizationName, PlatformCreator, DateTimeOffset.UnixEpoch, PlanTier.Enterprise);

                _ = await CatalogTables.PutOrganizationAsync(transaction, organization, cancellationToken).ConfigureAwait(false);
            }
            else if (organization.CreatedBy != PlatformCreator || organization.Plan != PlanTier.Enterprise)
            {
                return new Conflict("the platform organization id holds another organization", "platform_organization_taken");
            }

            foreach (var (name, database) in PlatformDatabases.Databases)
            {
                var owner = await CatalogTables.ProjectOrganizationAsync(transaction, database.ProjectId, cancellationToken).ConfigureAwait(false);
                if (owner is null)
                {
                    var project = new Project(database.ProjectId, organization.Id, name, DateTimeOffset.UnixEpoch);
                    _ = await CatalogTables.PutProjectAsync(transaction, project, cancellationToken).ConfigureAwait(false);
                }
                else if (owner != organization.Id)
                {
                    return new Conflict("a platform project id belongs to another organization", "platform_project_taken");
                }

            }

            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (tenants is not Done)
        {
            return tenants;
        }

        var placement = new DatabasePlacement(options.Region, [], 1);
        foreach (var (name, database) in PlatformDatabases.Databases)
        {
            var added = await AddDatabaseAsync(
                PlatformDatabases.OrganizationId,
                async transaction => await OrganizationProjectAsync(transaction, PlatformDatabases.OrganizationId, database.ProjectId, cancellationToken)
                    .ConfigureAwait(false) is { } tenant ? tenant : ManagementErrors.NotFound,
                name, sourceId: null, branchedFrom: null, cancellationToken, id: database.Id, placement: placement, createdAt: DateTimeOffset.UnixEpoch).ConfigureAwait(false);
            EnsurePlatformResult result = added switch
            {
                AddedDatabase => new Done(),
                Conflict error when error == ManagementErrors.NameTaken =>
                    await FindDatabaseRecordAsync(PlatformDatabases.OrganizationId, database.ProjectId, name, cancellationToken).ConfigureAwait(false) switch
                    {
                        ManagedDatabase existing when existing.Id == database.Id && existing.HomeRegion == options.Region && !existing.AllRegions && existing.Replicas.Count == 0 => new Done(),
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
