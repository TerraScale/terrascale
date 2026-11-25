using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Features.Management.Usage;
using TerraBase.Hosting;
using TerraScale.Contracts.ControlPlane;

namespace TerraBase.Features.Catalog;

internal readonly union OrganizationCreateOutcome(OrganizationRecord, StateConflict, BackendUnavailable);

internal readonly union OrganizationLookup(OrganizationRecord, ResourceNotFound, BackendUnavailable);

internal readonly union OrganizationPageOutcome(CatalogPage<OrganizationRecord>, BackendUnavailable);

internal readonly union CatalogUpdateOutcome(Completed, ResourceNotFound, BackendUnavailable);

internal readonly union OrganizationStoreOutcome(OrganizationRecord, BackendUnavailable);

internal readonly union CatalogDeleteOutcome(Completed, StateConflict, BackendUnavailable);

internal readonly union ProjectCreateOutcome(ProjectRecord, StateConflict, BackendUnavailable);

internal readonly union CatalogWriteOutcome(Completed, BackendUnavailable);

internal readonly union ProjectLookup(ProjectRecord, ResourceNotFound, BackendUnavailable);

internal readonly union ProjectPageOutcome(CatalogPage<ProjectRecord>, BackendUnavailable);

internal readonly union DatabaseLookup(DatabaseRecord, ResourceNotFound, BackendUnavailable);

internal readonly union DatabasePageOutcome(CatalogPage<DatabaseRecord>, BackendUnavailable);

internal readonly union DatabaseListOutcome(IReadOnlyList<DatabaseRecord>, BackendUnavailable);

internal readonly union DatabaseCountOutcome(long, BackendUnavailable);

internal readonly union DatabaseInsertOutcome(DatabaseRecord, StateConflict, BackendUnavailable);

internal readonly union DatabaseUpdateOutcome(DatabaseRecord, ResourceNotFound, StateConflict, BackendUnavailable);

internal sealed record DatabaseTombstoned(bool NameReused);

internal sealed record TombstonePurged;

internal sealed record TombstoneKept;

internal readonly union DatabaseExpiryOutcome(DatabaseTombstoned, StateConflict, BackendUnavailable);

internal readonly union DatabaseDeleteOutcome(DatabaseTombstoned, ResourceNotFound, StateConflict, BackendUnavailable);

internal readonly union TombstonePurgeOutcome(TombstonePurged, ResourceNotFound, BackendUnavailable);

internal readonly union DatabaseUpsertOutcome(Completed, TombstoneKept, StateConflict, BackendUnavailable);

internal readonly union DatabaseForgetOutcome(Completed, ResourceNotFound, StateConflict, BackendUnavailable);

internal readonly union DatabaseRowOutcome(bool, BackendUnavailable);

internal sealed class CatalogStore(SystemDatabase system)
{
    private const string DatabaseColumns =
        "id, project_id, name, regions, all_regions, default_consistency, strict, state, key_id, branched_from, previous_name, created_at, expires_at, version, storage_limit";

    private const int CleanupChunk = 256;

    public static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(7);

    public async ValueTask<OrganizationCreateOutcome> CreateOrganizationAsync(OrganizationRecord organization, CancellationToken cancellationToken) =>
        await system.WriteAsync(SqlBuild.Statement(
            "INSERT INTO organizations (id, name, created_by, created_at, plan, suspended) VALUES (?, ?, ?, ?, ?, ?)",
            SqlBuild.Id(organization.Id), SqlBuild.Text(organization.Name), SqlBuild.Text(organization.CreatedBy), SqlBuild.Time(organization.CreatedAt),
            SqlBuild.Text(organization.Plan), SqlBuild.Bool(organization.Suspended)), ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult => organization,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<OrganizationLookup> GetOrganizationAsync(Ulid id, CancellationToken cancellationToken)
    {
        var outcome = await system.QueryAsync(SqlBuild.Statement("SELECT id, name, created_by, created_at, plan, suspended FROM organizations WHERE id = ?", SqlBuild.Id(id)), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> { Count: > 0 } found => Organization(found[0]),
            IReadOnlyList<IReadOnlyList<SqlValue>> => new ResourceNotFound("the organization does not exist", "unknown_organization"),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<OrganizationPageOutcome> ListOrganizationsAsync(string createdBy, string? after, int limit, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement(
            "SELECT id, name, created_by, created_at, plan, suspended FROM organizations WHERE created_by = ?1 AND (?2 IS NULL OR id > ?2) ORDER BY id LIMIT ?3",
            SqlBuild.Text(createdBy), SqlBuild.OptionalText(after), SqlBuild.Integer(limit + 1)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Page(rows, limit, Organization, static item => item.Id.ToString()),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<CatalogUpdateOutcome> UpsertOrganizationAsync(
        OrganizationRecord organization,
        CancellationToken cancellationToken,
        ConsistencyMode mode = ConsistencyMode.Strong) =>
        await system.WriteAsync(SqlBuild.Statement(
            "INSERT INTO organizations (id, name, created_by, created_at, plan, suspended) VALUES (?, ?, ?, ?, ?, ?) "
            + "ON CONFLICT (id) DO UPDATE SET name = excluded.name, plan = excluded.plan, suspended = excluded.suspended",
            SqlBuild.Id(organization.Id), SqlBuild.Text(organization.Name), SqlBuild.Text(organization.CreatedBy), SqlBuild.Time(organization.CreatedAt),
            SqlBuild.Text(organization.Plan), SqlBuild.Bool(organization.Suspended)), mode, cancellationToken) switch
        {
            SqlWriteResult => new Completed(),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<CatalogUpdateOutcome> SetOrganizationPlanAsync(Ulid id, string plan, CancellationToken cancellationToken)
    {
        var outcome = await system.WriteAsync(SqlBuild.Statement("UPDATE organizations SET plan = ? WHERE id = ?", SqlBuild.Text(plan), SqlBuild.Id(id)), ConsistencyMode.Strong, cancellationToken);
        return outcome switch
        {
            SqlWriteResult { ResultSets: [{ RowsAffected: > 0 }, ..] } => new Completed(),
            SqlWriteResult => new ResourceNotFound("the organization does not exist", "unknown_organization"),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<OrganizationStoreOutcome> SetStorageOrganizationPlanAsync(
        Ulid id, string plan, DateTimeOffset now, CancellationToken cancellationToken) =>
        await system.WriteAsync(SqlBuild.Statement(
            "INSERT INTO organizations (id, name, created_by, created_at, plan, suspended) VALUES (?, ?, 'operator', ?, ?, 0) "
            + "ON CONFLICT (id) DO UPDATE SET plan = excluded.plan",
            SqlBuild.Id(id), SqlBuild.Id(id), SqlBuild.Time(now), SqlBuild.Text(plan)), ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult => await GetOrganizationAsync(id, cancellationToken) switch
            {
                OrganizationRecord organization => organization,
                ResourceNotFound => new OrganizationRecord(id, id.ToString(), "operator", now, plan, false),
                BackendUnavailable unavailable => unavailable,
            },
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<CatalogDeleteOutcome> DeleteOrganizationAsync(Ulid id, CancellationToken cancellationToken) =>
        await system.WriteAsync(
        [
            SqlBuild.Statement("DELETE FROM organizations WHERE id = ? AND NOT EXISTS (SELECT 1 FROM projects WHERE organization_id = ?)", SqlBuild.Id(id), SqlBuild.Id(id)),
        ], ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult { ResultSets: [{ RowsAffected: > 0 }, ..] } => new Completed(),
            SqlWriteResult => new StateConflict("delete the organization's projects first", "organization_not_empty"),
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<ProjectCreateOutcome> CreateProjectAsync(ProjectRecord project, CancellationToken cancellationToken) =>
        await system.WriteAsync(SqlBuild.Statement(
            "INSERT INTO projects (id, organization_id, name, created_at, plan) VALUES (?, ?, ?, ?, ?)",
            SqlBuild.Id(project.Id), SqlBuild.Id(project.OrganizationId), SqlBuild.Text(project.Name), SqlBuild.Time(project.CreatedAt), SqlBuild.OptionalText(project.Plan)),
            ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult => project,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<CatalogWriteOutcome> UpsertProjectAsync(
        ProjectRecord project,
        CancellationToken cancellationToken,
        ConsistencyMode mode = ConsistencyMode.Strong) =>
        await system.WriteAsync(SqlBuild.Statement(
            "INSERT INTO projects (id, organization_id, name, created_at, plan) VALUES (?, ?, ?, ?, ?) "
            + "ON CONFLICT (id) DO UPDATE SET organization_id = excluded.organization_id, name = excluded.name, plan = coalesce(excluded.plan, projects.plan)",
            SqlBuild.Id(project.Id), SqlBuild.Id(project.OrganizationId), SqlBuild.Text(project.Name), SqlBuild.Time(project.CreatedAt), SqlBuild.OptionalText(project.Plan)),
            mode, cancellationToken) switch
        {
            SqlWriteResult => new Completed(),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<ProjectLookup> EnrollProjectAsync(
        Ulid projectId, Ulid organizationId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await system.WriteAsync(
        [
            SqlBuild.Statement(
                "INSERT OR IGNORE INTO organizations (id, name, created_by, created_at, plan, suspended) VALUES (?, ?, 'operator', ?, 'hobby', 0)",
                SqlBuild.Id(organizationId), SqlBuild.Id(organizationId), SqlBuild.Time(now)),
            SqlBuild.Statement(
                "INSERT OR IGNORE INTO projects (id, organization_id, name, created_at, plan) VALUES (?, ?, ?, ?, NULL)",
                SqlBuild.Id(projectId), SqlBuild.Id(organizationId), SqlBuild.Id(projectId), SqlBuild.Time(now)),
        ], ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult => await GetProjectAsync(projectId, cancellationToken) switch
            {
                ProjectRecord project when project.OrganizationId == organizationId => project,
                ProjectRecord or ResourceNotFound => new ResourceNotFound("the project does not exist", "unknown_project"),
                BackendUnavailable unavailable => unavailable,
            },
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<CatalogUpdateOutcome> SetProjectPlanAsync(Ulid id, string plan, CancellationToken cancellationToken)
    {
        var outcome = await system.WriteAsync(SqlBuild.Statement("UPDATE projects SET plan = ? WHERE id = ?", SqlBuild.Text(plan), SqlBuild.Id(id)), ConsistencyMode.Strong, cancellationToken);
        return outcome switch
        {
            SqlWriteResult { ResultSets: [{ RowsAffected: > 0 }, ..] } => new Completed(),
            SqlWriteResult => new ResourceNotFound("the project does not exist", "unknown_project"),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ProjectLookup> GetProjectAsync(Ulid id, CancellationToken cancellationToken)
    {
        var outcome = await system.QueryAsync(SqlBuild.Statement("SELECT id, organization_id, name, created_at, plan FROM projects WHERE id = ?", SqlBuild.Id(id)), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> { Count: > 0 } found => Project(found[0]),
            IReadOnlyList<IReadOnlyList<SqlValue>> => new ResourceNotFound("the project does not exist", "unknown_project"),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ProjectPageOutcome> ListProjectsAsync(Ulid organizationId, string? after, int limit, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement(
            "SELECT id, organization_id, name, created_at, plan FROM projects WHERE organization_id = ?1 AND (?2 IS NULL OR id > ?2) ORDER BY id LIMIT ?3",
            SqlBuild.Id(organizationId), SqlBuild.OptionalText(after), SqlBuild.Integer(limit + 1)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Page(rows, limit, Project, static item => item.Id.ToString()),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<CatalogDeleteOutcome> DeleteProjectAsync(Ulid id, CancellationToken cancellationToken) =>
        await system.WriteAsync(
        [
            SqlBuild.Statement("DELETE FROM projects WHERE id = ? AND NOT EXISTS (SELECT 1 FROM databases WHERE project_id = ? AND state <> 'deleted')", SqlBuild.Id(id), SqlBuild.Id(id)),
            SqlBuild.Statement("DELETE FROM api_key_hashes WHERE key_id IN (SELECT id FROM api_keys WHERE project_id = ?) AND NOT EXISTS (SELECT 1 FROM projects WHERE id = ?)", SqlBuild.Id(id), SqlBuild.Id(id)),
            SqlBuild.Statement("DELETE FROM api_keys WHERE project_id = ? AND NOT EXISTS (SELECT 1 FROM projects WHERE id = ?)", SqlBuild.Id(id), SqlBuild.Id(id)),
        ], ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult { ResultSets: [{ RowsAffected: > 0 }, ..] } => new Completed(),
            SqlWriteResult => new StateConflict("delete the project's databases first", "project_not_empty"),
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<DatabaseLookup> FindDatabaseAsync(Ulid projectId, string reference, CancellationToken cancellationToken)
    {
        var statement = Ulid.TryParse(reference, out var id) && reference.Length == 26
            ? SqlBuild.Statement(string.Concat("SELECT ", DatabaseColumns, " FROM databases WHERE project_id = ? AND id = ? AND state <> 'deleted'"), SqlBuild.Id(projectId), SqlBuild.Id(id))
            : SqlBuild.Statement(string.Concat("SELECT ", DatabaseColumns, " FROM databases WHERE project_id = ? AND name = ? AND state <> 'deleted'"), SqlBuild.Id(projectId), SqlBuild.Text(reference));
        var outcome = await system.QueryAsync(statement, cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> { Count: > 0 } found => Database(found[0]),
            IReadOnlyList<IReadOnlyList<SqlValue>> => new ResourceNotFound("the database does not exist", "unknown_database"),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DatabaseLookup> GetDatabaseAsync(Ulid id, CancellationToken cancellationToken)
    {
        var outcome = await system.QueryAsync(SqlBuild.Statement(string.Concat("SELECT ", DatabaseColumns, " FROM databases WHERE id = ? AND state <> 'deleted'"), SqlBuild.Id(id)), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> { Count: > 0 } found => Database(found[0]),
            IReadOnlyList<IReadOnlyList<SqlValue>> => new ResourceNotFound("the database does not exist", "unknown_database"),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DatabasePageOutcome> ListDatabasesAsync(Ulid projectId, string? after, int limit, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement(
            string.Concat("SELECT ", DatabaseColumns, " FROM databases WHERE project_id = ?1 AND state <> 'deleted' AND (?2 IS NULL OR id > ?2) ORDER BY id LIMIT ?3"),
            SqlBuild.Id(projectId), SqlBuild.OptionalText(after), SqlBuild.Integer(limit + 1)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Page(rows, limit, Database, static item => item.Id.ToString()),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<DatabaseListOutcome> AllDatabasesAsync(string? after, int limit, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement(
            string.Concat("SELECT ", DatabaseColumns, " FROM databases WHERE state <> 'deleted' AND (?1 IS NULL OR id > ?1) ORDER BY id LIMIT ?2"), SqlBuild.OptionalText(after), SqlBuild.Integer(limit)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Records(rows),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<DatabaseListOutcome> ExpiredDatabasesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement(
            string.Concat("SELECT ", DatabaseColumns, " FROM databases WHERE state IN ('active', 'suspended') AND expires_at IS NOT NULL AND expires_at <= ? ORDER BY expires_at LIMIT ?"),
            SqlBuild.Time(now), SqlBuild.Integer(limit)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Records(rows),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<DatabaseCountOutcome> CountOrganizationDatabasesAsync(Ulid organizationId, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement(
            "SELECT count(*) FROM databases WHERE state <> 'deleted' AND project_id IN (SELECT id FROM projects WHERE organization_id = ?)", SqlBuild.Id(organizationId)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> { Count: > 0 } found => found[0].IntegerAt(0),
            IReadOnlyList<IReadOnlyList<SqlValue>> => 0,
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<DatabaseInsertOutcome> InsertDatabaseAsync(DatabaseRecord database, CancellationToken cancellationToken) =>
        await system.WriteAsync(
        [
            SqlBuild.Statement("INSERT INTO database_names (project_id, name, database_id) VALUES (?, ?, ?)", SqlBuild.Id(database.ProjectId), SqlBuild.Text(database.Name), SqlBuild.Id(database.Id)),
            Insert(database),
        ], ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult => database,
            StateConflict => new StateConflict("a database with that name already exists in the project", "database_exists"),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<DatabaseUpsertOutcome> UpsertDatabaseAsync(
        DatabaseRecord database,
        DatabaseRecord? existing,
        CancellationToken cancellationToken,
        ConsistencyMode mode = ConsistencyMode.Strong)
    {
        ArgumentNullException.ThrowIfNull(database);
        var moved = existing is null || existing.ProjectId != database.ProjectId || !string.Equals(existing.Name, database.Name, StringComparison.Ordinal);
        List<SqlStatement> statements = [existing is null ? Insert(database, "INSERT OR IGNORE") : Replace(database, existing)];
        if (moved)
        {
            statements.Add(SqlBuild.Statement(
                "INSERT INTO database_names (project_id, name, database_id) SELECT project_id, name, id FROM databases WHERE id = ? AND version = ? AND project_id = ? AND name = ? AND state <> 'deleted'",
                SqlBuild.Id(database.Id), SqlBuild.Integer(database.Version), SqlBuild.Id(database.ProjectId), SqlBuild.Text(database.Name)));
        }

        if (moved && existing is not null)
        {
            statements.Add(SqlBuild.Statement(
                "DELETE FROM database_names WHERE project_id = ? AND name = ? AND database_id = ? "
                + "AND EXISTS (SELECT 1 FROM databases WHERE id = ? AND project_id = ? AND name = ? AND version = ? AND state <> 'deleted')",
                SqlBuild.Id(existing.ProjectId), SqlBuild.Text(existing.Name), SqlBuild.Id(database.Id), SqlBuild.Id(database.Id), SqlBuild.Id(database.ProjectId),
                SqlBuild.Text(database.Name), SqlBuild.Integer(database.Version)));
        }

        var outcome = await system.WriteAsync(statements, mode, cancellationToken);
        return outcome switch
        {
            SqlWriteResult { ResultSets: [{ RowsAffected: > 0 }, ..] } => new Completed(),
            SqlWriteResult => await UnappliedAsync(database.Id, cancellationToken),
            StateConflict conflict => new StateConflict(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<DatabaseUpsertOutcome> UnappliedAsync(Ulid id, CancellationToken cancellationToken)
    {
        var row = await HasDatabaseRowAsync(id, cancellationToken);
        return row switch
        {
            bool present when present => await GetDatabaseAsync(id, cancellationToken) switch
            {
                DatabaseRecord => new StateConflict("the database changed concurrently; retry", "database_changed"),
                ResourceNotFound => new TombstoneKept(),
                BackendUnavailable unavailable => unavailable,
            },
            bool => new StateConflict("the database was removed concurrently; retry", "database_changed"),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private static SqlStatement Replace(DatabaseRecord database, DatabaseRecord existing) => SqlBuild.Statement(
        "UPDATE databases SET project_id = ?, name = ?, regions = ?, all_regions = ?, default_consistency = ?, strict = ?, state = ?, key_id = ?, branched_from = ?, previous_name = ?, "
        + "created_at = ?, expires_at = ?, version = ?, storage_limit = ? WHERE id = ? AND version = ? AND name = ? AND state <> 'deleted'",
        SqlBuild.Id(database.ProjectId), SqlBuild.Text(database.Name), SqlBuild.Text(string.Join(',', database.Regions)), SqlBuild.Bool(database.AllRegions),
        SqlBuild.Text(ConsistencyHeaders.Format(database.DefaultConsistency)), SqlBuild.Bool(database.Strict), SqlBuild.Text(State(database.State)), SqlBuild.Text(database.KeyId),
        database.BranchedFrom is { } source ? SqlBuild.Id(source) : SqlValue.Null, SqlBuild.OptionalText(database.PreviousName), SqlBuild.Time(database.CreatedAt),
        SqlBuild.OptionalTime(database.ExpiresAt), SqlBuild.Integer(database.Version), SqlBuild.Integer(database.StorageLimit),
        SqlBuild.Id(existing.Id), SqlBuild.Integer(existing.Version), SqlBuild.Text(existing.Name));

    public async ValueTask<DatabaseRowOutcome> HasDatabaseRowAsync(Ulid id, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement("SELECT 1 FROM databases WHERE id = ?", SqlBuild.Id(id)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => rows.Count > 0,
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<DatabaseUpdateOutcome> UpdateDatabaseAsync(DatabaseRecord current, DatabaseRecord updated, CancellationToken cancellationToken)
    {
        var renamed = !string.Equals(current.Name, updated.Name, StringComparison.Ordinal);
        List<SqlStatement> statements = [];
        if (renamed)
        {
            statements.Add(SqlBuild.Statement(
                "INSERT INTO database_names (project_id, name, database_id) SELECT project_id, ?, id FROM databases WHERE id = ? AND version = ? AND name = ? AND state <> 'deleted'",
                SqlBuild.Text(updated.Name), SqlBuild.Id(current.Id), SqlBuild.Integer(current.Version), SqlBuild.Text(current.Name)));
        }

        statements.Add(SqlBuild.Statement(
            "UPDATE databases SET name = ?, regions = ?, all_regions = ?, default_consistency = ?, state = ?, previous_name = ?, expires_at = ?, version = ? WHERE id = ? AND version = ? AND name = ? AND state <> 'deleted'",
            SqlBuild.Text(updated.Name), SqlBuild.Text(string.Join(',', updated.Regions)), SqlBuild.Bool(updated.AllRegions), SqlBuild.Text(ConsistencyHeaders.Format(updated.DefaultConsistency)),
            SqlBuild.Text(State(updated.State)), SqlBuild.OptionalText(updated.PreviousName), SqlBuild.OptionalTime(updated.ExpiresAt), SqlBuild.Integer(updated.Version),
            SqlBuild.Id(updated.Id), SqlBuild.Integer(current.Version), SqlBuild.Text(current.Name)));
        if (renamed)
        {
            statements.Add(SqlBuild.Statement(
                "DELETE FROM database_names WHERE project_id = ? AND name = ? AND database_id = ? AND EXISTS (SELECT 1 FROM databases WHERE id = ? AND name = ? AND version = ? AND state <> 'deleted')",
                SqlBuild.Id(current.ProjectId), SqlBuild.Text(current.Name), SqlBuild.Id(current.Id), SqlBuild.Id(current.Id), SqlBuild.Text(updated.Name), SqlBuild.Integer(updated.Version)));
        }

        var updateIndex = renamed ? 1 : 0;
        var outcome = await system.WriteAsync(statements, ConsistencyMode.Strong, cancellationToken);
        return outcome switch
        {
            SqlWriteResult written when written.ResultSets.Count > updateIndex && written.ResultSets[updateIndex].RowsAffected > 0 => updated,
            SqlWriteResult => new StateConflict("the database changed concurrently; retry", "database_changed"),
            StateConflict { Code: "serialization_conflict" } => new StateConflict("the database changed concurrently; retry", "database_changed"),
            StateConflict => new StateConflict("a database with that name already exists in the project", "database_exists"),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DatabasePageOutcome> DeletedDatabasesAsync(string? after, int limit, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement(
            string.Concat("SELECT ", DatabaseColumns, " FROM databases WHERE state = 'deleted' AND (?1 IS NULL OR id > ?1) ORDER BY id LIMIT ?2"),
            SqlBuild.OptionalText(after), SqlBuild.Integer(limit + 1)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Page(rows, limit, Database, static item => item.Id.ToString()),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<DatabaseForgetOutcome> ForgetDatabaseAsync(DatabaseRecord database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var outcome = await system.WriteAsync(
        [
            SqlBuild.Statement("DELETE FROM databases WHERE id = ? AND version = ? AND name = ? AND state <> 'deleted'",
                SqlBuild.Id(database.Id), SqlBuild.Integer(database.Version), SqlBuild.Text(database.Name)),
            SqlBuild.Statement("DELETE FROM database_names WHERE project_id = ? AND name = ? AND database_id = ? AND NOT EXISTS (SELECT 1 FROM databases WHERE id = ?)",
                SqlBuild.Id(database.ProjectId), SqlBuild.Text(database.Name), SqlBuild.Id(database.Id), SqlBuild.Id(database.Id)),
        ], ConsistencyMode.Strong, cancellationToken);
        return outcome switch
        {
            SqlWriteResult { ResultSets: [{ RowsAffected: > 0 }, ..] } => new Completed(),
            SqlWriteResult => await GetDatabaseAsync(database.Id, cancellationToken) switch
            {
                DatabaseRecord => new StateConflict("the database changed concurrently; retry", "database_changed"),
                ResourceNotFound missing => missing,
                BackendUnavailable unavailable => unavailable,
            },
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<TombstonePurgeOutcome> PurgeDatabaseAsync(DatabaseRecord database, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var outcome = await system.WriteAsync(
            SqlBuild.Statement("DELETE FROM databases WHERE id = ? AND state = 'deleted' AND (expires_at IS NULL OR expires_at <= ?)", SqlBuild.Id(database.Id), SqlBuild.Time(now)),
            ConsistencyMode.Strong, cancellationToken);
        return outcome switch
        {
            SqlWriteResult { ResultSets: [{ RowsAffected: > 0 }, ..] } => new TombstonePurged(),
            SqlWriteResult => new ResourceNotFound("the tombstone was already purged or is not due", "unknown_database"),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DatabaseDeleteOutcome> DeleteDatabaseAsync(DatabaseRecord database, DateTimeOffset purgeAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var outcome = await system.WriteAsync(
        [
            SqlBuild.Statement("UPDATE databases SET state = 'deleted', expires_at = ? WHERE id = ? AND version = ? AND name = ? AND state <> 'deleted'",
                SqlBuild.Time(purgeAfter), SqlBuild.Id(database.Id), SqlBuild.Integer(database.Version), SqlBuild.Text(database.Name)),
            ReleaseName(database),
        ], ConsistencyMode.Strong, cancellationToken);
        return outcome switch
        {
            SqlWriteResult { ResultSets: [{ RowsAffected: > 0 }, var names, ..] } => new DatabaseTombstoned(names.RowsAffected == 0),
            SqlWriteResult => await GetDatabaseAsync(database.Id, cancellationToken) switch
            {
                DatabaseRecord => new StateConflict("the database changed concurrently; retry", "database_changed"),
                ResourceNotFound missing => missing,
                BackendUnavailable unavailable => unavailable,
            },
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DatabaseExpiryOutcome> ExpireDatabaseAsync(DatabaseRecord database, DateTimeOffset now, DateTimeOffset purgeAfter, CancellationToken cancellationToken) =>
        await system.WriteAsync(
        [
            SqlBuild.Statement(
                "UPDATE databases SET state = 'deleted', expires_at = ? WHERE id = ? AND version = ? AND name = ? AND state IN ('active', 'suspended') AND expires_at IS NOT NULL AND expires_at <= ?",
                SqlBuild.Time(purgeAfter), SqlBuild.Id(database.Id), SqlBuild.Integer(database.Version), SqlBuild.Text(database.Name), SqlBuild.Time(now)),
            ReleaseName(database),
        ], ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult { ResultSets: [{ RowsAffected: > 0 }, var names, ..] } => new DatabaseTombstoned(names.RowsAffected == 0),
            SqlWriteResult => new StateConflict("the database's expiry changed concurrently", "database_changed"),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<CatalogWriteOutcome> CleanupDatabaseAsync(Ulid id, bool purged, CancellationToken cancellationToken)
    {
        var versions = await system.QueryAsync(SqlBuild.Statement("SELECT version FROM rules_versions WHERE database_id = ?", SqlBuild.Id(id)), cancellationToken);
        return versions switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => await CleanupKeysAsync(id, rows, purged, cancellationToken),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<CatalogWriteOutcome> CleanupKeysAsync(Ulid id, IReadOnlyList<IReadOnlyList<SqlValue>> versions, bool purged, CancellationToken cancellationToken)
    {
        var keys = await system.QueryAsync(SqlBuild.Statement("SELECT id FROM api_keys WHERE database_id = ?", SqlBuild.Id(id)), cancellationToken);
        return keys switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => await CleanupHashesAsync(id, versions, rows, purged, cancellationToken),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<CatalogWriteOutcome> CleanupHashesAsync(
        Ulid id, IReadOnlyList<IReadOnlyList<SqlValue>> versions, IReadOnlyList<IReadOnlyList<SqlValue>> keys, bool purged, CancellationToken cancellationToken)
    {
        var hashes = await system.QueryAsync(SqlBuild.Statement(
            "SELECT hash FROM api_key_hashes WHERE key_id IN (SELECT id FROM api_keys WHERE database_id = ?)", SqlBuild.Id(id)), cancellationToken);
        return hashes switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => await CleanupOwnedAsync(id, versions, keys, rows, purged, cancellationToken),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<CatalogWriteOutcome> CleanupOwnedAsync(
        Ulid id, IReadOnlyList<IReadOnlyList<SqlValue>> versions, IReadOnlyList<IReadOnlyList<SqlValue>> keys, IReadOnlyList<IReadOnlyList<SqlValue>> hashes,
        bool purged, CancellationToken cancellationToken)
    {
        List<SqlStatement> statements =
        [
            SqlBuild.Statement("DELETE FROM rules_current WHERE database_id = ?", SqlBuild.Id(id)),
            SqlBuild.Statement("DELETE FROM backup_policies WHERE database_id = ?", SqlBuild.Id(id)),
            .. versions.Select(row => SqlBuild.Statement("DELETE FROM rules_versions WHERE database_id = ? AND version = ?", SqlBuild.Id(id), SqlBuild.Text(row.TextAt(0)))),
            .. hashes.Select(static row => SqlBuild.Statement("DELETE FROM api_key_hashes WHERE hash = ?", SqlBuild.Text(row.TextAt(0)))),
            .. keys.Select(static row => SqlBuild.Statement("DELETE FROM api_keys WHERE id = ?", SqlBuild.Text(row.TextAt(0)))),
        ];
        foreach (var chunk in statements.Chunk(CleanupChunk))
        {
            var owned = await system.WriteAsync(chunk, ConsistencyMode.Strong, cancellationToken);
            if (owned switch
            {
                SqlWriteResult => null,
                StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
                BackendUnavailable unavailable => unavailable,
            } is { } failure)
            {
                return failure;
            }
        }

        return await CleanupTelemetryAsync(id, purged, cancellationToken);
    }

    private async ValueTask<CatalogWriteOutcome> CleanupTelemetryAsync(Ulid id, bool purged, CancellationToken cancellationToken)
    {
        List<SqlStatement> statements = [SqlBuild.Statement("DELETE FROM request_logs WHERE database_id = ?", SqlBuild.Id(id))];
        if (purged)
        {
            statements.Add(SqlBuild.Statement("DELETE FROM usage_counters WHERE scope = ?", SqlBuild.Text(UsageMeter.DatabaseScope(id))));
        }

        var outcome = await system.WriteAsync(statements, ConsistencyMode.Fast, cancellationToken);
        return outcome switch
        {
            SqlWriteResult => new Completed(),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private static SqlStatement ReleaseName(DatabaseRecord database) => SqlBuild.Statement(
        "DELETE FROM database_names WHERE project_id = ? AND name = ? AND database_id = ? AND EXISTS (SELECT 1 FROM databases WHERE id = ? AND state = 'deleted')",
        SqlBuild.Id(database.ProjectId), SqlBuild.Text(database.Name), SqlBuild.Id(database.Id), SqlBuild.Id(database.Id));

    public static string State(ControlDatabaseState state) => state switch
    {
        ControlDatabaseState.Active => "active",
        ControlDatabaseState.Suspended => "suspended",
        ControlDatabaseState.Deleted => "deleted",
        _ => throw new UnreachableException(),
    };

    private static ControlDatabaseState ParseState(string state) => state switch
    {
        "suspended" => ControlDatabaseState.Suspended,
        "deleted" => ControlDatabaseState.Deleted,
        _ => ControlDatabaseState.Active,
    };

    private static SqlStatement Insert(DatabaseRecord database, string verb = "INSERT") => SqlBuild.Statement(
        string.Concat(verb, " INTO databases (", DatabaseColumns, ") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"),
        SqlBuild.Id(database.Id), SqlBuild.Id(database.ProjectId), SqlBuild.Text(database.Name), SqlBuild.Text(string.Join(',', database.Regions)), SqlBuild.Bool(database.AllRegions),
        SqlBuild.Text(ConsistencyHeaders.Format(database.DefaultConsistency)), SqlBuild.Bool(database.Strict), SqlBuild.Text(State(database.State)), SqlBuild.Text(database.KeyId),
        database.BranchedFrom is { } source ? SqlBuild.Id(source) : SqlValue.Null, SqlBuild.OptionalText(database.PreviousName), SqlBuild.Time(database.CreatedAt),
        SqlBuild.OptionalTime(database.ExpiresAt), SqlBuild.Integer(database.Version), SqlBuild.Integer(database.StorageLimit));

    private static OrganizationRecord Organization(IReadOnlyList<SqlValue> row) =>
        new(row.IdAt(0), row.TextAt(1), row.TextAt(2), row.TimeAt(3), row.TextAt(4), row.BoolAt(5));

    private static ProjectRecord Project(IReadOnlyList<SqlValue> row) =>
        new(row.IdAt(0), row.IdAt(1), row.TextAt(2), row.TimeAt(3), row.OptionalTextAt(4));

    private static DatabaseRecord Database(IReadOnlyList<SqlValue> row) => new(
        row.IdAt(0), row.IdAt(1), row.TextAt(2), [.. row.TextAt(3).Split(',', StringSplitOptions.RemoveEmptyEntries)], row.BoolAt(4),
        ConsistencyHeaders.TryParse(row.TextAt(5), out var mode) ? mode : ConsistencyMode.Fast, row.BoolAt(6), ParseState(row.TextAt(7)), row.TextAt(8),
        row.OptionalTextAt(9) is { } source && Ulid.TryParse(source, out var branched) ? branched : null, row.OptionalTextAt(10), row.TimeAt(11), row.OptionalTimeAt(12),
        row.IntegerAt(13), row.IntegerAt(14));

    private static DatabaseListOutcome Records(IReadOnlyList<IReadOnlyList<SqlValue>> rows) => new([.. rows.Select(Database)]);

    private static CatalogPage<T> Page<T>(IReadOnlyList<IReadOnlyList<SqlValue>> rows, int limit, Func<IReadOnlyList<SqlValue>, T> read, Func<T, string> cursor)
    {
        List<T> items = [.. rows.Take(limit).Select(read)];
        return new CatalogPage<T>(items, rows.Count > limit && items.Count > 0 ? cursor(items[^1]) : null);
    }
}
