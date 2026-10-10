# 0005: Organizations are the tenant; storage is addressed by tenant

- Status: accepted (human decision, 2026-10-04)
- Scope: `src/TerraBase/Tenancy`, `Engine`, `Features/ApiKeys`, `Features/Documents`, `Features/Management`

## Context

`terrabase-rust/docs/control-plane-v1.md` says the dashboard owns organizations and projects, and
that one TerraBase tenant is one dashboard project. The ts-go SDK sends the dashboard
organization id as `X-TerraScale-Tenant-ID`. The Management API (`docs/management-api.md`) already
stores organizations and projects in TerraBase, so the two contracts disagreed on who owns them
and what a tenant is. Until now the engine and the data API addressed a database by name alone.

## Decision

- **TerraBase owns organizations and projects.** The **organization is the tenant**: it owns
  billing. A project is a folder of databases inside an organization.
- **Plan tier.** Every organization has a plan, `hobby` (default), `pro` or `enterprise`, stored on
  the organization and set with `PUT /v1/management/organizations/{organization}/plan`.
  Later quality-of-service work keys off it. Rate limits, quotas and fair scheduling are not part
  of this decision.
- **Physical layout.** One directory per tenant:
  `<data-directory>/<organizationId>/<projectId>/<databaseId>.db`. TerraBase's own state stays in
  `<data-directory>/_system.db`, which no tenant can address (`OpenSystemDatabaseAsync`).
  This supersedes the layout in [0002](0002-sqlite-storage-engine.md).
- **Tenant context.** `TenantContext(OrganizationId, Plan, ProjectId?, DatabaseId?)` is resolved
  once per request. Every `IStorageEngine` call that addresses a client database (create, open,
  copy, delete) takes one. Ids must be canonical uppercase ULIDs, otherwise the engine throws.
  A copy across organizations throws. Connection pools and the open-store cache are keyed by
  (organization, project, database).
- **Data API.** Routes are `/v1/projects/{project}/databases/{database}/…`, where `{database}` is the
  database name, unique per project (two organizations can both have `app`). The organization
  comes from the API key. Resolution is `DocumentRoute.TenantAsync` →
  `ITenantDirectory.FindDatabaseAsync(key organization, route project, name)` →
  `ApiKey.Reaches(tenant)`. A database that does not exist and one the key does not reach both
  answer `404` (`database not found`). `PUT /v1/projects/{project}/databases/{database}` creates the
  database in the catalog and on disk (`201`, or `200` if it exists) and returns
  `{id, projectId, name}`. A key narrowed to one database cannot create others.
- **API keys.** `POST /admin/api-keys` requires `organizationId` and accepts `projectId` and
  `databaseId` (which requires `projectId`). All must exist and nest, otherwise `400`. The response
  adds `organizationId`, `projectId` and `databaseId`. Keys stored before tenancy have no
  organization and answer `401`.
- **Management API.** The organization JSON gains `plan`. The database JSON loses `storageName`;
  the data API addresses a managed database as `/v1/projects/{projectId}/databases/{name}`.
  Management resolves the tenant (organization and plan, then project and database) inside the
  same SQLite transaction as its membership check and passes the `TenantContext` to every engine
  call, so a membership change cannot land between the check and the write.
- **No layout migration; one owner per data directory.** Nothing in production uses the flat layout:
  the C# TerraBase never ran in production, and GRU is being wiped (TS-470). So there is no in-place
  migration (human decision, 2026-10-04). At startup, before any database opens, the engine takes an
  exclusive lock (`FileShare.None`) on `<data-directory>/.terrabase.lock`. It is released only when
  the last connection closes: shutdown stops handing out connections and waits up to
  `shutdown-grace-seconds` (default 10) for transactions to return theirs. It closes nothing under a
  running transaction: when the grace expires it logs a warning with the count and returns, and the
  directory stays owned until those connections close or the process exits (the operating system then
  releases the lock).
  A second process on the same directory exits with code `2`:
  `data-directory is in use by another TerraBase process; stop it, then start again`.
  The lock file also records the layout: a server of this build that starts on an empty directory
  writes layout version `1` into it. Startup exits with code `2` and
  `data directory uses the pre-tenancy layout; start with an empty data directory (no automatic migration)`
  when the directory has files but no layout record, or has a database file (`<name>.db` or its
  `-wal`, `-shm`, `-journal`) directly in it other than `_system.db`. It opens, changes, renames
  and deletes none of those files. A layout record this build does not know (from a newer build) is
  refused the same way. Builds from before this change take no lock.
- **Replication.** The gRPC service is still a bootstrap stub that rejects every `Sync`, and
  `CommittedGroupBatch` carries no database identity yet. When replication applies batches, they
  must carry (organization, project, database) and go through the engine API, which accepts only a
  `TenantContext`.
- **Historical stub interfaces.** The initial, unimplemented interfaces took a `TenantContext`.
  The unused catalog, index advisor and SQL executor stubs were removed in TS-626; the implemented
  services live in `Features/Documents` and `Features/Management`.

## Consequences

- The data API route change is breaking. TerraBase is pre-1.0, so there is no compatibility shim
  for `/v1/databases/{database}/…`.
- API keys issued before tenancy no longer authenticate. They must be reissued with an
  `organizationId`.
- A data directory written before this change is refused: start this version with an empty data
  directory. Nothing in the old layout is read or converted.
- The plan is stored but nothing enforces it yet. Quotas and rate limiting are a follow-up
  quality-of-service ticket.
- Each data-plane request reads the catalog (three point lookups: organization, project, database
  name). There is no cache yet; add one when this shows up in latency measurements.
- Replication must carry tenant identity when it lands (see above). It is not designed here.
- Deleting a managed database still removes it from the catalog before deleting its file, as before
  this change. A crash in between leaves an unreachable file in the tenant's directory. Recovering such
  files is follow-up TS-480; this change does not sweep tenant directories at startup, because deleting files
  the catalog does not name would turn a damaged catalog into data loss.
- `control-plane-v1` in `terrabase-rust` still says the dashboard owns organizations and projects
  and that a tenant is a project. It must be updated there; that repository is not edited by this
  change.
