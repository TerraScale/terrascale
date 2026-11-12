# Dashboard state storage

The dashboard keeps its own data in SQL tables of its TerraBase database `ts-dashboard`, project
`00000000000000000000000005`. TerraBase seeds that database; the dashboard never creates it.

Every dashboard record is a row of a relational table, read and written through Entity Framework Core
(`DashboardDbContext`, the [TerraBase EF Core provider](../../sdk/dotnet/docs/entity-framework-core.md)). There is no
other storage path: MagicOnion reaches only TerraBase's product APIs (database management, customer SQL, backups,
usage), never the dashboard's own data.

See [organization checkout](organization-checkout.md) for the payment-before-creation state machine.

## Tables

### Access

`AddDashboardStorage` registers one pooled `IDbContextFactory<DashboardDbContext>` over a `TerraBaseSqlDatabase` on
TerraBase's private listener (`TS_DASHBOARD_TERRABASE_URL`, h2c). Its `HttpClient` adds two headers to every SQL
request and to the SQL WebSocket upgrade:

- `X-TerraBase-Peer-Credential`: the `TERRABASE_PEER_SECRET` the dashboard already holds; TerraBase treats the call
  as the platform operator.
- `TerraScale-Write: home`: every read and write runs at the database's home, so a flow reads its own writes in
  every region.

Request paths save with one `SaveChangesAsync` (one `/sql/execute` request, one transaction) or one
`ExecuteDeleteAsync`; none opens an explicit transaction. Storage calls appear in `Server-Timing` as `terrabase`
calls named `state/sql/execute`. Only writes (`SaveChangesAsync`, `ExecuteUpdateAsync`, `ExecuteDeleteAsync`) count as a
request's state change: a request reuses the account context it loaded until it writes, and a page reads a billing row or
Stripe customer once. A write that just saved a billing row starts the next change from that row and rereads only after
losing a concurrency race. Startup reads the migration history and takes the migration lock only when a migration is
pending.

### Schema

| Table | Holds | Keys and constraints |
|---|---|---|
| `auth_flows` | A sealed OIDC, step-up or alert-secret flow | `id` ULID; unique `token_hash` (SHA-256 of the cookie bearer); `purpose` is one of the three flow kinds; nonce 12 and tag 16 bytes; `expires_at > created_at` |
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
| `organizations` | Dashboard state of a ts-auth organization: kind, status, the plan it is entitled to with its billing status, payment-method flag and spend cap, internal billing policy and alerts, audit retention, pending and deleted project markers | `id` is the ts-auth ULID; kind `personal` or `business`; audit retention 1 to 90 days; `erasure_id` fence; `version` concurrency token |
| `organization_projects` | Dashboard settings of a ts-auth project: regions, status, default flag, spend cap, operation limits | `id` is the ts-auth ULID; foreign key to `organizations` with cascade delete; non-negative limits |
| `project_databases` | A project's database: name, placement, endpoint, status, limits, backup retention, branch source, expiry | `id` is the TerraBase ULID; foreign keys to `organizations` and `organization_projects`, both cascade; unique `(project_id, name)` |
| `audit_events` | An organization's dashboard audit event | `id` ULID; foreign key to `organizations` with cascade delete; index on `(organization_id, occurred_at)` |
| `invitation_reservations` | A plan member slot taken while ts-auth creates an invitation | `id` ULID (the operation id); foreign key to `organizations` with cascade delete; unique `(organization_id, invitation_id)` once ts-auth answered |
| `pending_organization_checkouts` | A user's organization draft after Stripe creates its Checkout Session | Key user ID; unique draft ULID and required nonempty session ID; plan is 1–128 characters and validated against the published catalog in code; interval is monthly or annual; version concurrency token; no organization exists yet |
| `billing` | An organization's billing binding: its Stripe checkout reservation and subscription projection, or its internal billing reservation, or an erasure marker | Key `organization_id`, foreign key to `organizations` with cascade delete, so one row per organization; `kind` is `stripe` or `internal`, `state` is `reserved`, `active` or `deleting`; `internal_reservation` (JSON policy snapshot and cap) exactly when `kind = 'internal'`; unique `customer_id` when set (`cus_…`); `erasure_id` ULID marks an erased organization and clears the rest; `revision >= 0`; `version` ULID concurrency token |
| `stripe_customers` | The Stripe customer that belongs to an organization | `id` ULID; unique `customer_id` (`cus_…`); unique `organization_id`, foreign key to `billing` with cascade delete |
| `stripe_checkouts` | A Checkout Session the dashboard created, bound to its reservation | `id` ULID; unique `checkout_id` (`cs_…`); foreign keys to `billing` and to `stripe_customers.customer_id`, both cascade; project ID empty or a ULID |
| `billing_changes` | An organization's latest plan change receipt and its Stripe idempotency binding | `id` ULID; unique `organization_id`, foreign key to `billing` with cascade delete; `action` is `change` or `undo`; `status` is `reserved`, `applying`, `applied` or `abandoned`; `revision >= 1`; `version` concurrency token |
| `internal_billing` | The one organization that owns internal billing, with its hidden plan ID | `id` ULID; unique `scope`, always `organization`, so at most one row exists; `organization_id` foreign key to `organizations` with cascade delete; plan ID is a ULID |
| `account_deletions` | A browser's account deletion operation: the sealed session, receipt and step-up authorization while it runs, then only a completion window | `id` ULID; unique `key_hash` (SHA-256 of the browser's 256-bit deletion cookie, lowercase hex); a running row has `user_id` and a 12-byte nonce, ciphertext and 16-byte tag, a completed row has none of them and a `completed_until`; indexes on `user_id` and `completed_until`; `version` concurrency token |
| `deletion_cleanup` | The deletion cleanup journal: one ts-auth deletion proof the dashboard accepted | `id` is the proof's ULID; `scope` is `account` (no organization), `organization` (organization absent or another than the subject) or `project` (organization ULID); `subject_id` ULID; index on `(scope, subject_id)` |
| `deletion_cleanup_targets` | A project or Stripe customer the journal entry found before removing its aggregate | Key `(cleanup_id, kind, target_id)`; `cleanup_id` foreign key to `deletion_cleanup` with cascade delete; `kind` is `project` (a ULID) or `customer` (1 to 255 characters) |
| `pending_restores` | A backup or point-in-time restore the dashboard started and still polls | `id` is the TerraBase operation ULID; organization, project and source database ULIDs; index on `(organization_id, project_id)` |

Grants (access and refresh tokens) stay encrypted with `TS_DASHBOARD_SESSION_ENCRYPTION_KEY` (AES-GCM) in
`nonce`, `ciphertext` and `tag` columns; owner, kind, device and expiry are plain columns, so listing, revocation and
expiry run in SQL. A refresh writes its lease with the row's `version` as concurrency token, so only one replica
rotates a grant; a stale token makes the save write nothing and the caller rereads.

A constraint violation (a duplicate saved query name, a second redemption of an MCP code, a second database of one
name in a project) fails the one request and writes nothing; the dashboard answers it as the feature's conflict
(`409`, `invalid_grant`). A save that loses a concurrency token race, or a create that finds the row already inserted,
raises EF Core's `DbUpdateConcurrencyException`; callers reread and apply their change again, and one that still loses
answers a retryable conflict, never `500`: `409` with `Retry-After: 1` and a message to try again, or `412` where an
endpoint maps the error through `DatabaseResults.Status`. A sign-in that still loses after the
rereads shows the sign-in failure page with its Try again link.

### Accounts and organizations

`AccountStore` and `OrganizationStore` load an account (with its notifications) or an organization (with its projects,
databases, audit events and invitation reservations) as one `AccountRecord` or `OrganizationRecord`. An account and its
notifications come back in one joined query. An organization read for a change loads the root row first, then its
child tables, so every child row is at least as new as the root's `version`; the read-only views of a request's
organizations load roots and children in parallel, one round trip, started as soon as ts-auth lists the
organizations. A change saves only the
rows that differ, inserts, updates and deletes, in one `SaveChangesAsync`, and writes a new `version` on the root row as
its concurrency token. A concurrent change of the same account or organization makes the save write nothing; the
caller rereads and applies its change again, at most four times, and then answers that retryable conflict. A change that only appends
audit events keeps the root's `version` but still checks it, so appends commute with each other (parallel sign-ins of
one account each record `session.created`) and still lose to any other change of the organization. A change that
already matches the stored rows writes nothing. ts-auth stays the source of
names, members, invitations and the project list: those fields are never stored.

The account row is keyed by the subject's SHA-256 (`subject_hash`), so creating it twice for one subject fails on the
unique index and the second sign-in rereads the first one's row.

### Erasure fence

An erased account or organization keeps its row with `erasure_id` set to the deletion proof's ULID. The fence clears
every profile column (an account keeps no `user_id`, name, email, phone or organization list), deletes the child rows
in the same save, and check constraints keep it that way. An organization fence then deletes every audit event of the
organization, which removes an append that committed between the fence's read and its save. A session, PAT or MCP token of an erased subject fails with
`401` on its next use (`AccountEnrollment`); a read or write of an erased organization answers `404`. Fencing an
account or organization that has no row yet inserts the fenced row, so a create racing the deletion fails on the key.

Alerts and log drains:

- A new alert signing secret is revealed after a `303` through a two-minute encrypted `auth_flows` hand-off, bound
  to the creating user, dashboard browser-session hash and project. Its cookie contains only a random bearer; only
  its hash is stored. A successful delete consumes the hand-off before rendering, so two concurrent GETs reveal it
  once. Expired or mismatched hand-offs reveal nothing; `StorageSweeper` deletes unread expired rows every ten minutes.
  Both the hand-off and the rule's persistent signing secret use AES-GCM with the dashboard session encryption key;
  neither is stored as plaintext, put in a URL or logged.
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

Billing rows are written compare-and-set: a writer reads the row, changes it and saves it with the `version` it read
as concurrency token and a new ULID as the next `version`. Stripe event deliveries, the checkout return page, the plan
reconciler and user actions on any replica race on the same `billing` row; the loser's save writes nothing, it rereads
the row and Stripe's current state and applies on top, so no write is lost and a redelivered event only repeats the
reconciliation. Create-only rows (a reservation, a Stripe customer, a checkout, the internal billing owner) are
inserted, and a primary key or unique violation means another writer created it first: the caller reads that row and
accepts it only if it matches. A second Stripe customer for an organization, or one Stripe customer for two
organizations, is refused by the unique indexes.

The `organizations` row keeps the plan the organization is entitled to, its billing status and whether a payment method
is on file; the billing writers set them from Stripe's subscription or the internal billing policy, and the `billing`
row keeps only the Stripe or internal binding behind them. Every billing row needs its organization's row. Deleting an
organization fences the `organizations` row first, so no checkout can start, and then the `billing` row.

### Deletions

`DeletionReconciler` replays every `deletion_cleanup` entry, ordered by `id`, before it asks ts-auth for new proofs,
and keeps the entries forever, so a regional write that lands after ts-auth's acknowledgement is removed again. An
organization entry's `acknowledged_at` and `billing_settled_at` end only its Stripe calls
([cleanup](direct-auth-deletion-cleanup.md)). A new
proof is recorded with its targets in one `SaveChangesAsync`; a second reconciler recording the same proof fails on the
primary key and reads the stored entry, which must carry the same scope, subject and organization.

An account deletion started in a browser inserts its `account_deletions` row before ts-auth is called. The row is found
only through the hash of the browser's deletion cookie. When the account proof is replayed, the reconciler opens each
running row of that `user_id`, checks the sealed session belongs to it and replaces the row, with its `version` as
concurrency token, by a completed row that keeps only `key_hash` and `completed_until` (15 days); expired completed rows
are deleted on the same pass.

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

`AllowAlertSecretHandoff` changes only the `auth_flows` purpose check constraint to admit `ts-dashboard-alert-secret`;
no column is added. During a rolling deploy, older instances query only the OIDC and step-up purposes and leave new
hand-offs unread; their sweeper can delete expired rows without decrypting them. A redirect reaching an older
instance cannot reveal the new secret, so the reveal requires an instance running the new code.

Each migration runs in one TerraBase transaction, capped at 5 seconds. Keep one table per migration: a single
migration creating seven tables took a median 4.7 s and up to 9.5 s while the E2E suite migrated about 30 databases
at once.

Startup (`DashboardStateInitializer`) runs `MigrateAsync`; concurrent replicas apply each migration once through the
provider's migration lock. Until it succeeds every request answers 503 and the initializer retries with jittered
backoff from 1 to 30 seconds; `StorageSweeper` and `DeletionReconciler` start their first pass only after it. `/readyz`
reports TerraBase unavailable while a migration is pending.

`AddDeletionCleanupSettlement` is forward-only. It adds the nullable `acknowledged_at` and `billing_settled_at` columns
to `deletion_cleanup` in place, but its `Down` cannot run on TerraBase: SQLite drops a column by rebuilding the table,
and the provider refuses, before any statement runs, to rebuild a table that another table references, as
`deletion_cleanup_targets` references `deletion_cleanup`. Rolling the code back needs no `Down`: the previous code reads
and writes `deletion_cleanup` only through EF Core, which names the columns it maps, so it ignores both columns and
leaves them NULL on the rows it inserts.

`DropProjectPlacementColumns` is a hand-written two-table migration inside one transaction. The provider refuses
its generated table rebuild because `project_databases` references `organization_projects`. Instead it copies both
tables, drops the referencing table before the referenced table, renames the copies and recreates their indexes.
The copies retain every remaining column, constraint and database-to-project link. Project regions become the old
non-empty primary followed by the other old list entries in their original order, removing any repeated primary
case-insensitively; an empty primary leaves the list unchanged. The first region is where new databases start.
A primary already first stays first; a primary later in the list moves first; a missing primary is prepended;
an empty list gains its non-empty primary; an empty primary preserves the original list.

Once this migration runs, an instance still running the previous version fails its organization reads because it
still selects the removed columns. Requests error; no write is lost. Upgrade those instances together or run `Down`
before rolling the code back. `Down` restores the required `region` and `backup_region` columns and fills both from
the first region, or the empty string for an empty list. The old code used the first listed region when no backup
was selected; the discarded historical backup choice cannot be recovered.

### Expiry

`StorageSweeper` runs every 10 minutes and deletes expired `auth_flows`, `sessions` (and with them their access tokens
and codes), expired `mcp_codes`, and MCP clients that never finished a first sign-in within 24 hours. It revokes an
expired browser session's grant at ts-auth (RFC 7009) before it deletes the row, as a request that finds its session
expired and sign-out do, so ts-auth prunes that sign-in's refresh tokens; a row it cannot decrypt holds no grant it can
revoke and is deleted.

Project and organization deletion delete `alert_rules` and `log_drains` (their events and cursors cascade) and the
organization's `allowance_alerts`.

## Tests

The journey in `e2e/` drives the dashboard against a real TerraBase built from the same commit; the dashboard
creates and migrates its database on start ([`e2e/AGENTS.md`](../../e2e/AGENTS.md)).

## Resets

Production data is not migrated between storage formats: a format change drops the `ts-dashboard` database during a
production re-bootstrap, then TerraBase re-seeds it and the dashboard migrates it on start.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
