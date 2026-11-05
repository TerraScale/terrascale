# SQL catalog (PIVOT E2)

TerraBase keeps its catalog of organizations, projects and databases in native SQL tables inside each node's system
database (`_system.db`, encrypted and WAL like every database file). Catalog entries are SQL rows. Code in `Features/Management/CatalogTables.cs` owns every statement.

## Tables

| Table | Rows |
|---|---|
| `catalog_organizations` | `id`, `name`, `name_key` (lowercase), `created_by`, `created_at` (UTC ticks), `plan`; unique `(created_by, name_key)` |
| `catalog_projects` | `id`, `organization_id`, `name`, `name_key`, `created_at`; unique `(organization_id, name_key)` |
| `catalog_databases` | `id`, `project_id`, `name`, `branched_from`, `created_at`, `home_region`, `placement_version`, `all_regions`; unique `(project_id, name)` |
| `catalog_database_replicas` | `(database_id, region)` for each replica region |
| `catalog_pending_cleanup` | a database whose files are being created or deleted: `database_id`, `organization_id`, `project_id`, `plan` |
| `catalog_retired_ids` | every deleted organization, project and database id; a retired id is never reused or enrolled again |

The tables are created with the catalog version row (`catalog/version` = `2`) in one transaction on a fresh node. A
node whose system database holds another catalog version answers `503 catalog_unsupported` and changes nothing; there
is no migration (greenfield). Each catalog write is one system-database transaction, so catalog rows commit atomically
with the backup, purge and sweep state they go with.

## Operations

- **Create:** a pending-cleanup row and the database file are created first; the catalog row is inserted (name checked
  against the unique index, the plan allowance checked by counting the organization's rows) and the pending row
  deleted in one transaction. A crash in between leaves a pending row, which startup recovery and the background pass
  finish or roll back.
- **Drop:** the catalog row is deleted, its id retired and a pending-cleanup row written in one transaction; the files
  are deleted after it commits, then the pending row is cleared.
- **List:** catalog pages are index scans in id order (`after` cursor, `limit` + 1 rows read).
- **Rename:** an update of `name` checked against the unique index, in the database's home region.
- **Plan allowance:** `count(*)` of the organization's databases in the same write transaction as the insert.

## Regions

A region's catalog holds the rows homed there: the databases whose home is the region and the organizations and
projects enrolled there. Only the home region writes a database's row. Other regions will read it from a read-only
replica of that region's system database pulled through region replicas (PIVOT R3); until then a region knows only
its own rows, and a database homed elsewhere answers as an unknown database ([cross-region.md](cross-region.md)).

## Tests

`tests/TerraBase.Fast/Catalog/SqlCatalogTests.cs` (create, list, drop, restart, rows in `catalog_databases`).
