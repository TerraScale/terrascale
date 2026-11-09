# Dashboard state storage

The dashboard keeps its own data in SQL tables of its TerraBase database `ts-dashboard`, project
`00000000000000000000000005`. TerraBase seeds that database; the dashboard never creates it.

The storage is moving from JSON-body tables to relational tables read and written through Entity Framework Core
(`DashboardDbContext`, the [TerraBase EF Core provider](../../sdk/dotnet/docs/entity-framework-core.md)). Both run
against the same database today:

| Data | Store |
|---|---|
| OIDC and step-up flows, browser sessions, PAT and MCP credentials, access tokens, MCP OAuth clients and codes, saved queries, SQL workspaces | Relational tables (below) |
| Accounts, identities, organizations, billing, Stripe customers, checkouts and plan changes, internal billing, allowance alerts, alert rules, log drains, account deletions and the deletion cleanup journal | JSON-body tables (`IDashboardState`, last section) |

## Relational tables

### Access

`AddDashboardStorage` registers one pooled `IDbContextFactory<DashboardDbContext>` over a `TerraBaseSqlDatabase` on
TerraBase's private listener (`TS_DASHBOARD_TERRABASE_URL`, h2c). Its `HttpClient` adds two headers to every SQL
request and to the SQL WebSocket upgrade:

- `X-TerraBase-Peer-Credential`: the `TERRABASE_PEER_SECRET` the dashboard already holds; TerraBase treats the call
  as the platform operator.
- `TerraScale-Write: home`: every read and write runs at the database's home, so a flow reads its own writes in
  every region, as the MagicOnion calls pinned to the home did before.

Request paths save with one `SaveChangesAsync` (one `/sql/execute` request, one transaction) or one
`ExecuteDeleteAsync`; none opens an explicit transaction. Storage calls appear in `Server-Timing` as `terrabase`
calls named `state/sql/execute`.

### Schema

| Table | Holds | Keys and constraints |
|---|---|---|
| `auth_flows` | A sealed OIDC or step-up flow | `id` ULID; unique `token_hash` (SHA-256 of the cookie bearer); `purpose` is one of the two flow kinds; nonce 12 and tag 16 bytes; `expires_at > created_at` |
| `sessions` | A sealed ts-auth grant: a browser session (`kind = 'browser'`) or a PAT/MCP credential (`kind = 'credential'`) | `id` ULID; unique `token_hash`, required for browser rows and absent for credentials; index on `user_id`; `version` ULID concurrency token for refresh leases |
| `access_tokens` | A personal access token | `id` ULID; unique `secret_hash`; unique `credential_id`, foreign key to `sessions` with cascade delete; `mcp_client_id` foreign key to `mcp_clients` |
| `mcp_clients` | A dynamically registered MCP OAuth client | `id` canonical ULID; 1 to 10 redirect URIs; lifecycle checks (`last_activity_at >= created_at`, first sign-in between them, `closed_at` after the last activity); `version` concurrency token |
| `mcp_codes` | A pending single-use authorization code | `id` ULID; unique `code_hash`; foreign keys to `mcp_clients` and to the credential in `sessions`, both cascade |
| `saved_queries` | A project's saved SQL query | `id` ULID; unique `(organization_id, project_id, database_id, name)` with a `NOCASE` name |
| `sql_workspaces` | A user's SQL draft, history and latest run receipt for one database | `id` ULID; unique `(organization_id, project_id, database_id, user_id)`; draft at most 16 KiB; at most 20 history entries; `version` concurrency token |

Grants (access and refresh tokens) stay encrypted with `TS_DASHBOARD_SESSION_ENCRYPTION_KEY` (AES-GCM) in
`nonce`, `ciphertext` and `tag` columns; owner, kind, device and expiry are plain columns, so listing, revocation and
expiry run in SQL. A refresh writes its lease with the row's `version` as concurrency token, so only one replica
rotates a grant; a stale token makes the save write nothing and the caller rereads.

A constraint violation (a duplicate saved query name, a second redemption of an MCP code) fails the one request and
writes nothing; the dashboard answers it as the feature's conflict (`409`, `invalid_grant`).

### Migrations

`Platform/Persistence/Migrations` holds one EF Core migration per table, generated with `dotnet ef` through the design
host `src/TerraScale.Dashboard.EntityFrameworkDesign`:

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/TerraScale.Dashboard \
    --startup-project src/TerraScale.Dashboard.EntityFrameworkDesign --output-dir Platform/Persistence/Migrations
sed -i '/^\s*\/\/\/ <inheritdoc \/>$/d' src/TerraScale.Dashboard/Platform/Persistence/Migrations/*.cs
dotnet format whitespace src/TerraScale.Dashboard/TerraScale.Dashboard.csproj
```

Each migration runs in one TerraBase transaction, capped at 5 seconds. Keep one table per migration: a single
migration creating seven tables took a median 4.7 s and up to 9.5 s while the E2E suite migrated about 30 databases
at once.

Startup (`DashboardStateInitializer`) runs `MigrateAsync`; concurrent replicas apply each migration once through the
provider's migration lock. Until it succeeds every request answers 503 and the initializer retries with jittered
backoff from 1 to 30 seconds. `/readyz` reports TerraBase unavailable while a migration is pending.

### Expiry

`StorageSweeper` runs every 10 minutes and deletes expired `auth_flows`, `sessions` (and with them their access tokens
and codes), expired `mcp_codes`, and MCP clients that never finished a first sign-in within 24 hours.

## JSON-body tables

The remaining tables keep the original shape, written over MagicOnion (`IManagementService.ExecuteSqlAsync`) by
`TerraBaseDashboardState`:

```sql
CREATE TABLE IF NOT EXISTS <table> (id TEXT PRIMARY KEY NOT NULL, version TEXT NOT NULL, body TEXT NOT NULL)
```

`DashboardStateSchema.Tables` lists them. `id` is the record's natural key, `version` a new ULID on every write and
`body` the record as JSON. Writes are compare-and-set on `version`; a conflict raises `StateConflictException`
(HTTP 412) and callers reread and retry. Startup creates the tables before running the migrations.

## Tests

The E2E suite (`tests/TerraScale.Dashboard.E2E`) runs the relational tables on a real TerraBase built from the same
commit by `scripts/dashboard/TerraScale.Dashboard.slnx` (`Demo/TerraBaseStorage.cs`). Each test process starts one
TerraBase with the test peer secret and one project. Each `DemoEnvironment` gets its own database there, with the script `dotnet ef` generates from the committed migrations applied in one request (history rows
included), and its hosts replace only the `TerraBaseSqlDatabase` registration; their startup `MigrateAsync` finds
nothing pending. Journeys read rows through `DemoEnvironment.StorageAsync()`. The production-process journey
(`Platform/OciProductionJourneyTests.cs`) starts the built program against a fresh `ts-dashboard` platform database
and waits for `MigrateAsync` to make it ready.

The JSON-body tables still run on the in-memory `TestState`, and on `TestSqlEngine` behind the real MagicOnion
services in `StateSdkScenario`.

## Resets

Production data is not migrated between storage formats: a format change drops the `ts-dashboard` database during a
production re-bootstrap, then TerraBase re-seeds it and the dashboard migrates it on start.
