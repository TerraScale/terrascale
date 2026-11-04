# Dashboard state storage

The dashboard keeps its own data (sessions, accounts, organizations, access tokens,
MCP OAuth clients and codes, saved queries, SQL workspaces, billing, alerts and log
drains) in SQL tables of its TerraBase database `ts-dashboard`, project
`00000000000000000000000005`. TerraBase seeds that database; the dashboard never
creates it.

## Access

Every call goes over MagicOnion to `IManagementService.ExecuteSqlAsync`, the RPC form
of `POST /v1/projects/{project}/databases/ts-dashboard/sql/execute`, on TerraBase's
private listener with the shared peer credential. On that listener TerraBase treats the
call as the platform operator with every scope of the project. One RPC is one SQL
transaction. The dashboard uses no document, collection or transaction API for its
own data.

## Schema

`DashboardStateSchema.Tables` lists 27 tables, one per record type. Each has the
same shape:

```sql
CREATE TABLE IF NOT EXISTS <table> (id TEXT PRIMARY KEY NOT NULL, version TEXT NOT NULL, body TEXT NOT NULL)
```

- `id` is the record's natural key (a ULID, hash, email or composite key).
- `version` is a new ULID on every write.
- `body` is the record as JSON. Scoped reads filter with `json_extract(body, '$.Field')`.

Expression indexes cover the scoped cleanup reads:
`dashboard_sql_workspaces (OrganizationId, ProjectId, DatabaseId)` and
`dashboard_sql_database_deletions (OrganizationId, ProjectId)`.

Startup runs every `CREATE ... IF NOT EXISTS` statement in one request, then checks
`sqlite_master`. `/readyz` repeats that check, so a dropped table fails readiness at once.
Until the schema initializes, every request answers 503 and the initializer retries
with jittered backoff from 1 to 30 seconds.

## Writes

| Operation | SQL | Conflict |
|---|---|---|
| Create | `INSERT INTO t (id, version, body) VALUES (?, ?, ?)` | primary key violation |
| Replace | `UPDATE t SET version = ?, body = ? WHERE id = ? AND version = ?` | 0 rows changed |
| Delete | `DELETE FROM t WHERE id = ? AND version = ?` | 0 rows changed |

A conflict raises `StateConflictException` (HTTP 412). Callers reread and retry, so
concurrent writers never overwrite each other.

## Paging

`PageAsync` reads `WHERE id > ?` in `id` order and fetches one row more than the page
to find the next cursor (the last `id`). Scoped pages add a `json_extract` equality
for each owner field. Cleanup pages read 32 rows, and a field list projects only
those fields with `json_object`.

## Resets

The schema has no migrations. A format change drops the `ts-dashboard` database
during a production re-bootstrap, then re-seeds it.

## Tests

`tests/TerraScale.Dashboard.E2E/Demo/StateSdkScenario.cs` serves the SQL RPC from a
real Turso engine (`TestSqlEngine`, the vendored `Turso.Data.Sqlite` driver) behind
the real MagicOnion services. Every other journey uses the in-memory `TestState`, as
does the `DEMO=true` host.
