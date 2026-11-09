# Dashboard state storage

The dashboard keeps its own data in SQL tables of its TerraBase database `ts-dashboard`, project
`00000000000000000000000005`. TerraBase seeds that database; the dashboard never creates it.

The storage is moving from JSON-body tables to relational tables read and written through Entity Framework Core
(`DashboardDbContext`, the [TerraBase EF Core provider](../../sdk/dotnet/docs/entity-framework-core.md)). Both run
against the same database today:

| Data | Store |
|---|---|
| OIDC and step-up flows, browser sessions, PAT and MCP credentials, access tokens, MCP OAuth clients and codes, saved queries, SQL workspaces, allowance alerts, alert rules and their events, log drains and their cursors, accounts and their notifications, organizations with their projects, project databases, audit events and invitation reservations | Relational tables (below) |
| Billing, Stripe customers, checkouts and plan changes, internal billing, account deletions and the deletion cleanup journal | JSON-body tables (`IDashboardState`, last section) |

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
| `allowance_alerts` | An organization's operations (80 % and 100 %) or spending-alert notice for one UTC month, with its email outcome | `id` ULID, also the inbox notification id; unique `(organization_id, billing_month, metric, threshold)`; `metric` is `operations`, `storage` or `spend`; `threshold` 80 or 100 (spend only 100); `email_status` is `pending` or a final ts-auth answer |
| `alert_rules` | A project's metric alert rule and its current state | `id` ULID; unique `(project_id, slot)` with `slot` 0 to 19, so a project holds at most 20 rules; index on `organization_id`; known metric; threshold above 0 and at most 1e12; webhook URL and sealed signing secret together or not at all; `firing` needs `since` and `alert_id`; `version` concurrency token |
| `alert_events` | One firing or resolved transition still waiting for its email or webhook delivery | `id` ULID, the webhook `X-TerraScale-Delivery`; `rule_id` foreign key to `alert_rules` with cascade delete; index `(rule_id, id)`; a row exists only while the email or the webhook is `pending`; at most 3 webhook attempts |
| `log_drains` | A project's OTLP/HTTP log drain | `id` ULID; unique `project_id`; index on `organization_id`; `https://` URL up to 2048 characters; header name and sealed value together or not at all; `revision` concurrency token |
| `log_drain_cursors` | The drain's TerraBase request-log cursor for one database | `id` ULID; unique `(drain_id, database_id)`; `drain_id` foreign key to `log_drains` with cascade delete; cursor is a ULID |
| `accounts` | A signed-in user's dashboard profile, onboarding step and selected organization and project | `id` ULID (the public account id); unique `subject_hash` (SHA-256 of the ts-auth subject) and `user_id`; `erasure_id` fence (below); `version` concurrency token |
| `notifications` | An account's inbox item | key `(account_id, id)`, foreign key to `accounts` with cascade delete; status `unread` or `read` |
| `organizations` | Dashboard state of a ts-auth organization: kind, status, plan and billing fields, internal billing policy and alerts, audit retention, pending and deleted project markers | `id` is the ts-auth ULID; kind `personal` or `business`; audit retention 1 to 90 days; `erasure_id` fence; `version` concurrency token |
| `organization_projects` | Dashboard settings of a ts-auth project: regions, status, default flag, spend cap, operation limits | `id` is the ts-auth ULID; foreign key to `organizations` with cascade delete; non-negative limits |
| `project_databases` | A project's database: name, placement, endpoint, status, limits, backup retention, branch source, expiry | `id` is the TerraBase ULID; foreign keys to `organizations` and `organization_projects`, both cascade; unique `(project_id, name)` |
| `audit_events` | An organization's dashboard audit event | `id` ULID; foreign key to `organizations` with cascade delete; index on `(organization_id, occurred_at)` |
| `invitation_reservations` | A plan member slot taken while ts-auth creates an invitation | `id` ULID (the operation id); foreign key to `organizations` with cascade delete; unique `(organization_id, invitation_id)` once ts-auth answered |

Grants (access and refresh tokens) stay encrypted with `TS_DASHBOARD_SESSION_ENCRYPTION_KEY` (AES-GCM) in
`nonce`, `ciphertext` and `tag` columns; owner, kind, device and expiry are plain columns, so listing, revocation and
expiry run in SQL. A refresh writes its lease with the row's `version` as concurrency token, so only one replica
rotates a grant; a stale token makes the save write nothing and the caller rereads.

A constraint violation (a duplicate saved query name, a second redemption of an MCP code, a second database of one
name in a project) fails the one request and writes nothing; the dashboard answers it as the feature's conflict
(`409`, `invalid_grant`).

### Accounts and organizations

`AccountStore` and `OrganizationStore` load an account (with its notifications) or an organization (with its projects,
databases, audit events and invitation reservations) as one `AccountRecord` or `OrganizationRecord`: the root row
first, then its child tables, so every child row is at least as new as the root's `version`. A change saves only the
rows that differ, inserts, updates and deletes, in one `SaveChangesAsync`, and writes a new `version` on the root row as
its concurrency token. A concurrent change of the same account or organization makes the save write nothing; the
caller rereads and applies its change again, at most four times, and then answers `412`. ts-auth stays the source of
names, members, invitations and the project list: those fields are never stored.

The account row is keyed by the subject's SHA-256 (`subject_hash`), so creating it twice for one subject fails on the
unique index and the second sign-in rereads the first one's row.

### Erasure fence

An erased account or organization keeps its row with `erasure_id` set to the deletion proof's ULID. The fence clears
every profile column (an account keeps no `user_id`, name, email, phone or organization list), deletes the child rows
in the same save, and check constraints keep it that way. A session, PAT or MCP token of an erased subject fails with
`401` on its next use (`AccountEnrollment`); a read or write of an erased organization answers `404`. Fencing an
account or organization that has no row yet inserts the fenced row, so a create racing the deletion fails on the key.

Alerts and log drains:

- Creating an alert rule takes the lowest free slot; a create that loses the slot to a concurrent one takes the next,
  and a project with all 20 slots taken answers `409`. A threshold change writes a new `version`, so a worker that
  evaluated the old threshold saves nothing and evaluates again a minute later.
- The alert worker finds projects through `alert_rules`. A state change saves the rule, and for a rule that emails or posts a webhook inserts its event, in one
  `SaveChangesAsync`; a rule keeps its 10 newest pending events. After delivery a finished event is deleted and a
  pending one keeps its statuses and attempts. Deleting a rule deletes its events.
- The allowance worker records each notice once through the unique index: a concurrent insert of the same notice
  fails and the worker rereads. A raised spending alert replaces the month's `spend` row in the same save, and
  rows of earlier months are deleted. Owners' inboxes copy the rows by `id`.
- A log drain edit keeps the row, its cursors and a stored header whose name is unchanged and value left blank; it
  writes a new `revision` and clears the delivery status. The drain worker saves cursors and status with the
  `revision` it read, so a save that races an edit writes nothing and the new endpoint receives those requests.

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

Project and organization deletion delete `alert_rules` and `log_drains` (their events and cursors cascade) and the
organization's `allowance_alerts`.

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
