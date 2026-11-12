using System.Diagnostics;
using TerraBase.Engine;
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
        var tenants = await ReadAsync<PlatformState>(transaction => PlatformTenantsAsync(transaction, create: false, cancellationToken), static error => error, cancellationToken)
            .ConfigureAwait(false);
        if (tenants is NotFound)
        {
            tenants = await WriteAsync<PlatformState>(transaction => PlatformTenantsAsync(transaction, create: true, cancellationToken), static error => error, cancellationToken)
                .ConfigureAwait(false);
        }

        if (tenants is not Done)
        {
            return Settled(tenants);
        }

        List<Conflict> conflicts = [];
        Unavailable? pending = null;
        foreach (var (name, database) in PlatformDatabases.Databases)
        {
            switch (await SeedDatabaseAsync(name, database, cancellationToken).ConfigureAwait(false))
            {
                case Conflict conflict:
                    conflicts.Add(conflict);
                    break;
                case Unavailable unavailable:
                    pending ??= unavailable;
                    break;
            }
        }

        return pending is not null ? new PlatformPending(conflicts, pending)
            : conflicts.Contains(PlatformDatabases.Orphaned) ? new PlatformOrphaned(conflicts)
            : conflicts.Count == 0 ? new Done() : new PlatformConflicts(conflicts);
    }

    private async ValueTask<PlatformState> SeedDatabaseAsync(string name, (string Id, string ProjectId) database, CancellationToken cancellationToken)
    {
        var state = await ReadAsync<PlatformState>(transaction => PlatformDatabaseAsync(transaction, name, database, cancellationToken), static error => error, cancellationToken)
            .ConfigureAwait(false);
        if (state is not NotFound)
        {
            return state;
        }

        var tenant = new TenantContext(PlatformDatabases.OrganizationId, PlanTier.Enterprise, database.ProjectId, database.Id);
        return engine.HasDatabaseFiles(tenant)
            && !await ReadAsync(transaction => OwnedFileAsync(transaction, tenant, cancellationToken), static _ => true, cancellationToken).ConfigureAwait(false)
                ? PlatformDatabases.Orphaned
                : await AddPlatformDatabaseAsync(name, database, cancellationToken).ConfigureAwait(false);
    }

    private static EnsurePlatformResult Settled(PlatformState state) => state switch
    {
        Done done => done,
        Conflict conflict => new PlatformConflicts([conflict]),
        Unavailable unavailable => new PlatformPending([], unavailable),
        NotFound => throw new UnreachableException(),
    };

    private static async ValueTask<PlatformState> PlatformTenantsAsync(IStorageTransaction transaction, bool create, CancellationToken cancellationToken)
    {
        var organization = await CatalogTables.OrganizationAsync(transaction, PlatformDatabases.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (organization is null)
        {
            if (!create)
            {
                return ManagementErrors.NotFound;
            }

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
                if (!create)
                {
                    return ManagementErrors.NotFound;
                }

                var project = new Project(database.ProjectId, organization.Id, name, DateTimeOffset.UnixEpoch);
                _ = await CatalogTables.PutProjectAsync(transaction, project, cancellationToken).ConfigureAwait(false);
            }
            else if (owner != organization.Id)
            {
                return new Conflict("a platform project id belongs to another organization", "platform_project_taken");
            }

        }

        return new Done();
    }

    private async ValueTask<PlatformState> PlatformDatabaseAsync(
        IStorageTransaction transaction, string name, (string Id, string ProjectId) database, CancellationToken cancellationToken)
    {
        if (await IsProjectClosedAsync(transaction, database.ProjectId, cancellationToken).ConfigureAwait(false))
        {
            return new Conflict("a platform project is closed", "platform_project_closed");
        }

        if (await CatalogTables.DatabaseNamedAsync(transaction, database.ProjectId, name, cancellationToken).ConfigureAwait(false) is { } id)
        {
            return id == database.Id && await CatalogTables.DatabaseAsync(transaction, database.ProjectId, id, cancellationToken).ConfigureAwait(false) is { } existing
                && existing.HomeRegion == options.Region && !existing.AllRegions && existing.Replicas.Count == 0
                    ? new Done()
                    : new Conflict("a platform database name holds another database, or the platform database with another placement", "platform_database_taken");
        }

        return await DatabasePurge.IsDeletedAsync(transaction, database.Id, cancellationToken).ConfigureAwait(false)
            ? new Conflict("a platform database id is permanently deleted", "platform_database_purged")
            : ManagementErrors.NotFound;
    }

    private async ValueTask<PlatformState> AddPlatformDatabaseAsync(string name, (string Id, string ProjectId) database, CancellationToken cancellationToken) =>
        await AddDatabaseAsync(
            PlatformDatabases.OrganizationId,
            async transaction => await OrganizationProjectAsync(transaction, PlatformDatabases.OrganizationId, database.ProjectId, cancellationToken)
                .ConfigureAwait(false) is { } tenant ? tenant : ManagementErrors.NotFound,
            name, sourceId: null, branchedFrom: null, cancellationToken, id: database.Id, placement: new DatabasePlacement(options.Region, [], 1), createdAt: DateTimeOffset.UnixEpoch)
            .ConfigureAwait(false) switch
        {
            AddedDatabase => new Done(),
            Conflict error when error == ManagementErrors.NameTaken => new Unavailable("a platform database was created while it was seeded", "platform_database_raced"),
            Conflict error => error,
            Unavailable error => error,
            NotFound or Forbidden or QuotaExceeded => new Unavailable("the platform project is not ready", "platform_project_unready"),
        };

    private readonly union PlatformState(Done, Conflict, NotFound, Unavailable);
}
