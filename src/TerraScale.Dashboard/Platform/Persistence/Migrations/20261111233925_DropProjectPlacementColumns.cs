using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class DropProjectPlacementColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE organization_projects_regions (
                    id TEXT NOT NULL CONSTRAINT PK_organization_projects PRIMARY KEY,
                    organization_id TEXT NOT NULL,
                    regions TEXT NOT NULL,
                    status TEXT NOT NULL,
                    is_default INTEGER NOT NULL,
                    spend_cap_cents INTEGER NOT NULL,
                    read_limit INTEGER NOT NULL,
                    write_limit INTEGER NOT NULL,
                    replication_limit INTEGER NOT NULL,
                    CONSTRAINT FK_organization_projects_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE,
                    CONSTRAINT ck_organization_projects_id CHECK (length(id) = 26),
                    CONSTRAINT ck_organization_projects_limits CHECK (read_limit >= 0 AND write_limit >= 0 AND replication_limit >= 0),
                    CONSTRAINT ck_organization_projects_regions CHECK (json_type(regions) = 'array')
                )
                """);
            migrationBuilder.Sql("""
                INSERT INTO organization_projects_regions (id, organization_id, regions, status, is_default, spend_cap_cents, read_limit, write_limit, replication_limit)
                SELECT id, organization_id,
                    CASE
                        WHEN length(region) > 0 THEN (
                            SELECT json_group_array(value) FROM (
                                SELECT organization_projects.region AS value, -1 AS position
                                UNION ALL
                                SELECT value, key FROM json_each(organization_projects.regions)
                                WHERE upper(value) <> upper(organization_projects.region)
                                ORDER BY position))
                        ELSE regions
                    END,
                    status, is_default, spend_cap_cents, read_limit, write_limit, replication_limit
                FROM organization_projects
                """);
            migrationBuilder.Sql("""
                CREATE TABLE project_databases_kept (
                    id TEXT NOT NULL CONSTRAINT PK_project_databases PRIMARY KEY,
                    organization_id TEXT NOT NULL,
                    project_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    previous_name TEXT NULL,
                    conflict_of TEXT NULL,
                    region TEXT NOT NULL,
                    regions TEXT NOT NULL,
                    all_regions INTEGER NOT NULL,
                    placement_revision INTEGER NOT NULL,
                    endpoint TEXT NOT NULL,
                    status TEXT NOT NULL,
                    spend_cap_cents INTEGER NOT NULL,
                    read_limit INTEGER NOT NULL,
                    write_limit INTEGER NOT NULL,
                    replication_limit INTEGER NOT NULL,
                    backup_retention_days INTEGER NOT NULL,
                    branched_from TEXT NULL,
                    created_at TEXT NOT NULL,
                    expires_at TEXT NULL,
                    CONSTRAINT FK_project_databases_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE,
                    CONSTRAINT FK_project_databases_organization_projects_project_id FOREIGN KEY (project_id) REFERENCES organization_projects_regions (id) ON DELETE CASCADE,
                    CONSTRAINT ck_project_databases_id CHECK (length(id) = 26),
                    CONSTRAINT ck_project_databases_name CHECK (length(name) > 0),
                    CONSTRAINT ck_project_databases_regions CHECK (json_type(regions) = 'array'),
                    CONSTRAINT ck_project_databases_placement_revision CHECK (placement_revision >= 0),
                    CONSTRAINT ck_project_databases_limits CHECK (read_limit >= 0 AND write_limit >= 0 AND replication_limit >= 0 AND backup_retention_days >= 0)
                )
                """);
            migrationBuilder.Sql("""
                INSERT INTO project_databases_kept (id, organization_id, project_id, name, previous_name, conflict_of, region, regions, all_regions, placement_revision, endpoint, status, spend_cap_cents, read_limit, write_limit, replication_limit, backup_retention_days, branched_from, created_at, expires_at)
                SELECT id, organization_id, project_id, name, previous_name, conflict_of, region, regions, all_regions, placement_revision, endpoint, status, spend_cap_cents, read_limit, write_limit, replication_limit, backup_retention_days, branched_from, created_at, expires_at
                FROM project_databases
                """);
            migrationBuilder.Sql("DROP TABLE project_databases");
            migrationBuilder.Sql("DROP TABLE organization_projects");
            migrationBuilder.Sql("ALTER TABLE organization_projects_regions RENAME TO organization_projects");
            migrationBuilder.Sql("ALTER TABLE project_databases_kept RENAME TO project_databases");
            migrationBuilder.Sql("CREATE INDEX IX_organization_projects_organization_id ON organization_projects (organization_id)");
            migrationBuilder.Sql("CREATE INDEX IX_project_databases_organization_id ON project_databases (organization_id)");
            migrationBuilder.Sql("CREATE UNIQUE INDEX IX_project_databases_project_id_name ON project_databases (project_id, name)");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "backup_region",
                table: "organization_projects",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "region",
                table: "organization_projects",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE organization_projects
                SET region = coalesce(json_extract(regions, '$[0]'), ''),
                    backup_region = coalesce(json_extract(regions, '$[0]'), '');
                """);
        }
    }
}
