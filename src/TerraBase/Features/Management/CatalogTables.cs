using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal static class CatalogTables
{
    private const int MaxRows = 1_000;
    private const long MaxBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly Dictionary<string, object?> NoNamed = [];

    private static readonly string[] Schema =
    [
        """
        CREATE TABLE IF NOT EXISTS catalog_organizations(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL,
            created_by TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            plan INTEGER NOT NULL)
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS catalog_organizations_name ON catalog_organizations(created_by, name_key)",
        """
        CREATE TABLE IF NOT EXISTS catalog_projects(
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL,
            created_at INTEGER NOT NULL)
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS catalog_projects_name ON catalog_projects(organization_id, name_key)",
        """
        CREATE TABLE IF NOT EXISTS catalog_databases(
            id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL,
            name TEXT NOT NULL,
            branched_from TEXT,
            created_at INTEGER NOT NULL,
            home_region TEXT,
            placement_version INTEGER NOT NULL,
            all_regions INTEGER NOT NULL)
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS catalog_databases_name ON catalog_databases(project_id, name)",
        """
        CREATE TABLE IF NOT EXISTS catalog_database_replicas(
            database_id TEXT NOT NULL,
            region TEXT NOT NULL,
            PRIMARY KEY (database_id, region))
        """,
        "CREATE TABLE IF NOT EXISTS catalog_retired_ids(id TEXT PRIMARY KEY)",
        """
        CREATE TABLE IF NOT EXISTS catalog_pending_cleanup(
            database_id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL,
            project_id TEXT NOT NULL,
            plan INTEGER NOT NULL)
        """,
    ];

    private const string Live = "NOT EXISTS (SELECT 1 FROM catalog_retired_ids r WHERE r.id = d.id)";
    private const string OrganizationColumns = "id, name, created_by, created_at, plan";
    private const string ProjectColumns = "id, organization_id, name, created_at";
    private const string DatabaseColumns = "id, project_id, name, branched_from, created_at, home_region, placement_version, all_regions";

    public static async ValueTask CreateAsync(IStorageTransaction transaction, CancellationToken cancellationToken)
    {
        foreach (var statement in Schema)
        {
            _ = await ExecuteAsync(transaction, Sql(statement), cancellationToken).ConfigureAwait(false);
        }
    }

    public static async ValueTask<bool> ExistAsync(IStorageTransaction transaction, CancellationToken cancellationToken) =>
        (await RowsAsync(transaction, Sql("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'catalog_databases'"), cancellationToken).ConfigureAwait(false)).Count > 0;

    public static async ValueTask<Organization?> OrganizationAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken) =>
        (await RowsAsync(transaction, Sql($"SELECT {OrganizationColumns} FROM catalog_organizations WHERE id = ?", id), cancellationToken).ConfigureAwait(false))
            .Select(ReadOrganization).FirstOrDefault();

    public static async ValueTask<string?> OrganizationNamedAsync(IStorageTransaction transaction, string createdBy, string nameKey, CancellationToken cancellationToken) =>
        (await RowsAsync(transaction, Sql("SELECT id FROM catalog_organizations WHERE created_by = ? AND name_key = ?", createdBy, nameKey), cancellationToken).ConfigureAwait(false))
            .Select(row => (string)row[0]!).FirstOrDefault();

    public static async ValueTask<ManagementPage<Organization>> OrganizationsCreatedByAsync(
        IStorageTransaction transaction, string createdBy, string? after, int limit, CancellationToken cancellationToken) =>
        Page((await RowsAsync(transaction, Sql(
            $"SELECT {OrganizationColumns} FROM catalog_organizations WHERE created_by = ? AND id > ? ORDER BY id LIMIT ?", createdBy, after ?? "", (long)limit + 1), cancellationToken)
            .ConfigureAwait(false)).Select(ReadOrganization).ToList(), limit, organization => organization.Id);

    public static async ValueTask<ManagementPage<string>> OrganizationIdsAsync(IStorageTransaction transaction, string? after, int limit, CancellationToken cancellationToken) =>
        Page((await RowsAsync(transaction, Sql("SELECT id FROM catalog_organizations WHERE id > ? ORDER BY id LIMIT ?", after ?? "", (long)limit + 1), cancellationToken)
            .ConfigureAwait(false)).Select(row => (string)row[0]!).ToList(), limit, id => id);

    public static ValueTask<long> PutOrganizationAsync(IStorageTransaction transaction, Organization organization, CancellationToken cancellationToken) =>
        ExecuteAsync(transaction, Sql(
            """
            INSERT INTO catalog_organizations(id, name, name_key, created_by, created_at, plan) VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, name_key = excluded.name_key, created_by = excluded.created_by,
                created_at = excluded.created_at, plan = excluded.plan
            """,
            organization.Id, organization.Name, Key(organization.Name), organization.CreatedBy, organization.CreatedAt.UtcTicks, (long)organization.Plan), cancellationToken);

    public static async ValueTask DeleteOrganizationAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken)
    {
        _ = await ExecuteAsync(transaction, Sql("DELETE FROM catalog_organizations WHERE id = ?", id), cancellationToken).ConfigureAwait(false);
        await RetireAsync(transaction, id, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<bool> RetiredAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken) =>
        (await RowsAsync(transaction, Sql("SELECT 1 FROM catalog_retired_ids WHERE id = ?", id), cancellationToken).ConfigureAwait(false)).Count > 0;

    private static async ValueTask RetireAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken) =>
        _ = await ExecuteAsync(transaction, Sql("INSERT OR IGNORE INTO catalog_retired_ids(id) VALUES (?)", id), cancellationToken).ConfigureAwait(false);

    public static async ValueTask<Project?> ProjectAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken) =>
        (await RowsAsync(transaction, Sql($"SELECT {ProjectColumns} FROM catalog_projects WHERE id = ?", id), cancellationToken).ConfigureAwait(false))
            .Select(ReadProject).FirstOrDefault();

    public static async ValueTask<string?> ProjectOrganizationAsync(IStorageTransaction transaction, string projectId, CancellationToken cancellationToken) =>
        (await ProjectAsync(transaction, projectId, cancellationToken).ConfigureAwait(false))?.OrganizationId;

    public static async ValueTask<Project?> OrganizationProjectAsync(IStorageTransaction transaction, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await ProjectAsync(transaction, projectId, cancellationToken).ConfigureAwait(false) is { } project && project.OrganizationId == organizationId ? project : null;

    public static async ValueTask<string?> ProjectNamedAsync(IStorageTransaction transaction, string organizationId, string nameKey, CancellationToken cancellationToken) =>
        (await RowsAsync(transaction, Sql("SELECT id FROM catalog_projects WHERE organization_id = ? AND name_key = ?", organizationId, nameKey), cancellationToken).ConfigureAwait(false))
            .Select(row => (string)row[0]!).FirstOrDefault();

    public static async ValueTask<ManagementPage<Project>> ProjectsAsync(
        IStorageTransaction transaction, string organizationId, string? after, int limit, CancellationToken cancellationToken) =>
        Page((await RowsAsync(transaction, Sql(
            $"SELECT {ProjectColumns} FROM catalog_projects WHERE organization_id = ? AND id > ? ORDER BY id LIMIT ?", organizationId, after ?? "", (long)limit + 1), cancellationToken)
            .ConfigureAwait(false)).Select(ReadProject).ToList(), limit, project => project.Id);

    public static async ValueTask<ManagementPage<Project>> AllProjectsAsync(IStorageTransaction transaction, string? after, int limit, CancellationToken cancellationToken) =>
        Page((await RowsAsync(transaction, Sql(
            $"SELECT {ProjectColumns} FROM catalog_projects WHERE id > ? ORDER BY id LIMIT ?", after ?? "", (long)limit + 1), cancellationToken)
            .ConfigureAwait(false)).Select(ReadProject).ToList(), limit, project => project.Id);

    public static async ValueTask<bool> HasProjectsAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken) =>
        (await RowsAsync(transaction, Sql("SELECT 1 FROM catalog_projects WHERE organization_id = ? LIMIT 1", organizationId), cancellationToken).ConfigureAwait(false)).Count > 0;

    public static ValueTask<long> PutProjectAsync(IStorageTransaction transaction, Project project, CancellationToken cancellationToken) =>
        ExecuteAsync(transaction, Sql(
            """
            INSERT INTO catalog_projects(id, organization_id, name, name_key, created_at) VALUES (?, ?, ?, ?, ?)
            ON CONFLICT(id) DO UPDATE SET organization_id = excluded.organization_id, name = excluded.name, name_key = excluded.name_key,
                created_at = excluded.created_at
            """,
            project.Id, project.OrganizationId, project.Name, Key(project.Name), project.CreatedAt.UtcTicks), cancellationToken);

    public static async ValueTask DeleteProjectAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken)
    {
        _ = await ExecuteAsync(transaction, Sql("DELETE FROM catalog_projects WHERE id = ?", id), cancellationToken).ConfigureAwait(false);
        await RetireAsync(transaction, id, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<ManagedDatabase?> DatabaseAsync(IStorageTransaction transaction, string projectId, string id, CancellationToken cancellationToken) =>
        await DatabaseAsync(transaction, id, cancellationToken).ConfigureAwait(false) is { } database && database.ProjectId == projectId ? database : null;

    public static async ValueTask<ManagedDatabase?> DatabaseAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(transaction, Sql($"SELECT {DatabaseColumns} FROM catalog_databases d WHERE id = ? AND {Live}", id), cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : (await WithReplicasAsync(transaction, [ReadDatabase(rows[0])], cancellationToken).ConfigureAwait(false))[0];
    }

    public static async ValueTask<string?> DatabaseNamedAsync(IStorageTransaction transaction, string projectId, string name, CancellationToken cancellationToken) =>
        (await RowsAsync(transaction, Sql($"SELECT id FROM catalog_databases d WHERE project_id = ? AND name = ? AND {Live}", projectId, name), cancellationToken).ConfigureAwait(false))
            .Select(row => (string)row[0]!).FirstOrDefault();

    public static async ValueTask RenameDatabaseAsync(IStorageTransaction transaction, string id, string name, CancellationToken cancellationToken) =>
        _ = await RowsAsync(transaction, Sql("UPDATE catalog_databases SET name = ? WHERE id = ?", name, id), cancellationToken).ConfigureAwait(false);

    public static async ValueTask<ManagementPage<ManagedDatabase>> DatabasesAsync(
        IStorageTransaction transaction, string projectId, string? after, int limit, CancellationToken cancellationToken)
    {
        var page = Page((await RowsAsync(transaction, Sql(
            $"SELECT {DatabaseColumns} FROM catalog_databases d WHERE project_id = ? AND id > ? AND {Live} ORDER BY id LIMIT ?", projectId, after ?? "", (long)limit + 1), cancellationToken)
            .ConfigureAwait(false)).Select(ReadDatabase).ToList(), limit, database => database.Id);
        return page with { Items = await WithReplicasAsync(transaction, page.Items, cancellationToken).ConfigureAwait(false) };
    }

    public static async ValueTask<bool> HasDatabasesAsync(IStorageTransaction transaction, string projectId, CancellationToken cancellationToken) =>
        (await RowsAsync(transaction, Sql($"SELECT 1 FROM catalog_databases d WHERE project_id = ? AND {Live} LIMIT 1", projectId), cancellationToken).ConfigureAwait(false)).Count > 0;

    public static async ValueTask<long> CountOrganizationDatabasesAsync(
        IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken, string? defaultRegion = null, IReadOnlyList<string>? homedElsewhere = null)
    {
        List<object?> args = [organizationId];
        var excluded = string.Empty;
        if (homedElsewhere is { Count: > 0 })
        {
            args.Add(defaultRegion);
            excluded = $" AND coalesce(d.home_region, ?) NOT IN ({string.Join(", ", homedElsewhere.Select(_ => "?"))})";
            args.AddRange(homedElsewhere);
        }

        return (long)(await RowsAsync(transaction, Sql(
            $"SELECT count(*) FROM catalog_databases d JOIN catalog_projects p ON p.id = d.project_id WHERE p.organization_id = ? AND {Live}{excluded}", [.. args]), cancellationToken)
            .ConfigureAwait(false))[0][0]!;
    }

    public static async ValueTask<long> CountOrganizationDatabasesHomedAsync(
        IStorageTransaction transaction, string organizationId, string region, CancellationToken cancellationToken) =>
        (long)(await RowsAsync(transaction, Sql(
            $"SELECT count(*) FROM catalog_databases d JOIN catalog_projects p ON p.id = d.project_id WHERE p.organization_id = ? AND coalesce(d.home_region, ?) = ? AND {Live}",
            organizationId, region, region), cancellationToken)
            .ConfigureAwait(false))[0][0]!;

    public static async ValueTask<IReadOnlyList<TenantContext>> DatabasesHomedAsync(
        IStorageTransaction transaction, string? homeRegion, string defaultRegion, TenantContext? after, int limit, CancellationToken cancellationToken) =>
        [.. (await RowsAsync(transaction, Sql(
            $"""
            SELECT p.organization_id, d.project_id, d.id FROM catalog_databases d JOIN catalog_projects p ON p.id = d.project_id
            WHERE coalesce(d.home_region, ?) = ? AND (d.project_id > ? OR d.project_id = ? AND d.id > ?) AND {Live}
            ORDER BY d.project_id, d.id LIMIT ?
            """,
            defaultRegion, homeRegion, after?.ProjectId ?? "", after?.ProjectId ?? "", after?.DatabaseId ?? "", (long)limit), cancellationToken).ConfigureAwait(false))
            .Select(row => new TenantContext((string)row[0]!, PlanTier.Hobby, (string)row[1]!, (string)row[2]!))];

    public static async ValueTask<IReadOnlyList<(string Project, string Id)>> AllRegionDatabasesAsync(
        IStorageTransaction transaction, string region, CancellationToken cancellationToken) =>
        [.. (await RowsAsync(transaction, Sql(
            $"SELECT d.project_id, d.id FROM catalog_databases d WHERE d.all_regions = 1 AND coalesce(d.home_region, ?) = ? AND {Live}", region, region), cancellationToken)
            .ConfigureAwait(false)).Select(row => ((string)row[0]!, (string)row[1]!))];

    public static async ValueTask PutDatabaseAsync(IStorageTransaction transaction, ManagedDatabase database, CancellationToken cancellationToken)
    {
        _ = await ExecuteAsync(transaction, Sql(
            """
            INSERT INTO catalog_databases(id, project_id, name, branched_from, created_at, home_region, placement_version, all_regions) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(id) DO UPDATE SET project_id = excluded.project_id, name = excluded.name, branched_from = excluded.branched_from,
                created_at = excluded.created_at, home_region = excluded.home_region, placement_version = excluded.placement_version, all_regions = excluded.all_regions
            """,
            database.Id, database.ProjectId, database.Name, database.BranchedFrom, database.CreatedAt.UtcTicks, database.HomeRegion, database.PlacementVersion,
            database.AllRegions ? 1L : 0L), cancellationToken).ConfigureAwait(false);
        _ = await ExecuteAsync(transaction, Sql("DELETE FROM catalog_database_replicas WHERE database_id = ?", database.Id), cancellationToken).ConfigureAwait(false);
        await DatabaseTtl.WriteAsync(transaction, database.Id, database.ExpiresAt, cancellationToken).ConfigureAwait(false);
        foreach (var region in database.Replicas)
        {
            _ = await ExecuteAsync(transaction, Sql("INSERT OR IGNORE INTO catalog_database_replicas(database_id, region) VALUES (?, ?)", database.Id, region), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public static async ValueTask<bool> DeleteDatabaseAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken)
    {
        _ = await ExecuteAsync(transaction, Sql("DELETE FROM catalog_database_replicas WHERE database_id = ?", id), cancellationToken).ConfigureAwait(false);
        await DatabaseTtl.WriteAsync(transaction, id, null, cancellationToken).ConfigureAwait(false);
        await RetireAsync(transaction, id, cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(transaction, Sql("DELETE FROM catalog_databases WHERE id = ?", id), cancellationToken).ConfigureAwait(false) > 0;
    }

    public static async ValueTask<TenantContext?> PendingCleanupAsync(IStorageTransaction transaction, string databaseId, CancellationToken cancellationToken) =>
        (await RowsAsync(transaction, Sql(
            "SELECT organization_id, plan, project_id, database_id FROM catalog_pending_cleanup WHERE database_id = ?", databaseId), cancellationToken).ConfigureAwait(false))
            .Select(ReadTenant).FirstOrDefault();

    public static async ValueTask<ManagementPage<TenantContext>> PendingCleanupsAsync(IStorageTransaction transaction, string? after, int limit, CancellationToken cancellationToken) =>
        Page((await RowsAsync(transaction, Sql(
            "SELECT organization_id, plan, project_id, database_id FROM catalog_pending_cleanup WHERE database_id > ? ORDER BY database_id LIMIT ?", after ?? "", (long)limit + 1), cancellationToken)
            .ConfigureAwait(false)).Select(ReadTenant).ToList(), limit, tenant => tenant.DatabaseId!);

    public static ValueTask<long> PutPendingCleanupAsync(IStorageTransaction transaction, TenantContext database, CancellationToken cancellationToken) =>
        ExecuteAsync(transaction, Sql(
            """
            INSERT INTO catalog_pending_cleanup(database_id, organization_id, project_id, plan) VALUES (?, ?, ?, ?)
            ON CONFLICT(database_id) DO UPDATE SET organization_id = excluded.organization_id, project_id = excluded.project_id, plan = excluded.plan
            """,
            database.DatabaseId, database.OrganizationId, database.ProjectId, (long)database.Plan), cancellationToken);

    public static async ValueTask<bool> DeletePendingCleanupAsync(IStorageTransaction transaction, string databaseId, CancellationToken cancellationToken) =>
        await ExecuteAsync(transaction, Sql("DELETE FROM catalog_pending_cleanup WHERE database_id = ?", databaseId), cancellationToken).ConfigureAwait(false) > 0;

    public static string Key(string name) => name.ToLowerInvariant();

    private static async ValueTask<IReadOnlyList<ManagedDatabase>> WithReplicasAsync(
        IStorageTransaction transaction, IReadOnlyList<ManagedDatabase> databases, CancellationToken cancellationToken)
    {
        if (databases.Count == 0)
        {
            return databases;
        }

        var ids = databases.Select(database => (object?)database.Id).ToArray();
        var replicas = (await RowsAsync(transaction, Sql(
            $"SELECT database_id, region FROM catalog_database_replicas WHERE database_id IN ({string.Join(", ", ids.Select(_ => "?"))}) ORDER BY database_id, region", ids), cancellationToken)
            .ConfigureAwait(false)).ToLookup(row => (string)row[0]!, row => (string)row[1]!);
        List<ManagedDatabase> placed = [];
        foreach (var database in databases)
        {
            placed.Add(database with
            {
                ReplicaRegions = replicas.Contains(database.Id) ? [.. replicas[database.Id]] : database.ReplicaRegions,
                ExpiresAt = await DatabaseTtl.ReadAsync(transaction, database.Id, cancellationToken).ConfigureAwait(false),
            });
        }

        return placed;
    }

    private static ManagementPage<T> Page<T>(List<T> items, int limit, Func<T, string> id)
    {
        if (items.Count <= limit)
        {
            return new ManagementPage<T>(items, null);
        }

        items.RemoveAt(items.Count - 1);
        return new ManagementPage<T>(items, id(items[^1]));
    }

    private static Organization ReadOrganization(object?[] row) =>
        new((string)row[0]!, (string)row[1]!, (string)row[2]!, Time(row[3]), (PlanTier)(long)row[4]!);

    private static Project ReadProject(object?[] row) => new((string)row[0]!, (string)row[1]!, (string)row[2]!, Time(row[3]));

    private static ManagedDatabase ReadDatabase(object?[] row) =>
        new((string)row[0]!, (string)row[1]!, (string)row[2]!, (string?)row[3], Time(row[4]), (string?)row[5], null, (long)row[6]!, (long)row[7]! != 0);

    private static TenantContext ReadTenant(object?[] row) => new((string)row[0]!, (PlanTier)(long)row[1]!, (string)row[2]!, (string)row[3]!);

    private static DateTimeOffset Time(object? ticks) => new((long)ticks!, TimeSpan.Zero);

    private static SqlStatement Sql(string sql, params object?[] args) => new(sql, args, NoNamed, MaxRows, MaxBytes, Timeout);

    private static async ValueTask<IReadOnlyList<object?[]>> RowsAsync(IStorageTransaction transaction, SqlStatement statement, CancellationToken cancellationToken) =>
        (await transaction.ExecuteStatementAsync(statement, cancellationToken).ConfigureAwait(false)).Rows;

    private static async ValueTask<long> ExecuteAsync(IStorageTransaction transaction, SqlStatement statement, CancellationToken cancellationToken) =>
        (await transaction.ExecuteStatementAsync(statement, cancellationToken).ConfigureAwait(false)).RowsAffected;
}
