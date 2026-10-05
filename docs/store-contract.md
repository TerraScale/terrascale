# Store contract

The dashboard reads and writes its state through four small interfaces, each
with an in-memory implementation that the `TS_DASHBOARD_PRODUCTION_DEMO=1`
deployment and the test suites use:

| Interface | Memory implementation | State |
|---|---|---|
| `core.Store` (`internal/core/store.go`) | `memstore.Store` (`internal/core/memstore`) | accounts, users, organizations and everything under them, plans, admin actions |
| `session.Store` (`internal/platform/session/store.go`) | `session.MemoryStore` | browser and bearer sessions |
| `auth.DeviceGrants` (`internal/features/auth/device_grants.go`) | `auth.MemoryDeviceGrants` | CLI device authorization grants |
| `throttle.Store` (`internal/platform/throttle/store.go`) | `throttle.MemoryStore` | password-guessing token buckets |

The CSRF key is configuration, not state: `TS_DASHBOARD_CSRF_SECRET` (at least
32 bytes, required outside the demo, random per process in the demo).

This document describes the TerraBase adapter
(`internal/adapters/terrabase`). Phase 1 uses Documents v1 only, including
organizations, memberships, projects and database records. The Management
mapping is phase 2 (TS-521). It lists what each
interface promises, which invariants `memstore` enforces today with one global
lock and full scans, and how each one maps onto the TerraBase v1 contract
(`terrabase/docs/api-v1.md`, `terrabase/docs/query-v1.md`). The adapter talks
to TerraBase through ts-go only.

## Conventions

- Every method takes the request context first.
- Lookups return `core.ErrNotFound` instead of a `bool`.
- Domain failures use the `core` sentinels: `ErrNotFound`, `ErrConflict`,
  `ErrForbidden`, `ErrUnauthorized`, `ErrInvalid` and `ErrDatabaseLimit`.
  Database failures wrap `core.ErrUnavailable`, which `memstore` never returns.
  `core.Can` reports every domain failure as `ErrForbidden` and passes
  `ErrUnavailable` through.
- Returned values are isolated copies.
- A write that records an external side effect already done (a provider
  call, a Stripe session) runs on `context.WithoutCancel(ctx)`, so a canceled
  request cannot leave the record behind the provider. Handlers report
  `ErrUnavailable` as 503.
- Aggregate callbacks (`Update`, `UpdateAs`, `UpdateAsWithPlan`,
  `UpdateAccountAsAdmin`, `UpdateOrganization`, `ViewAs`) are pure functions
  of the aggregate they receive. They may run more than once, and they never
  call the store or do I/O. Every feature callback was checked for this when
  ctx was threaded through.

## Aggregates

`core.Store` exposes two aggregates, and the adapter keeps their shape:

- An **account**: the user, the IDs of the organizations it belongs to, the
  selected organization and project, onboarding state, `AuthVersion`, personal
  access tokens, notifications, audit events and demo profile security.
- An **organization**: its projects (each with databases, API keys, saved
  queries, shared result configurations, backup policies and its ts-auth
  instance), members, invitations, checkout sessions and billing.

`Update`/`UpdateAs` hand the callback the account with every live
organization it belongs to. The adapter's aggregate repository works in four
steps:

1. **Load** the user document, the user's memberships, the documents of those
   organizations and their children, and the plans. Remember every document's
   `version`.
2. **Apply** the callback to a deep copy.
3. **Diff** the copy against what was loaded, document by document. Then
   commit one `POST /transactions` with `put`/`patch` ops carrying `if_match`
   on every changed document, `create` (`if_none_match:"*"`) on new ones and
   `delete` with `if_match` on removed ones.
4. **On `412 precondition_failed`**, reload and rerun the callback, at most a
   few times. Then fail with `ErrUnavailable`, which means contention.

Map `409 unique_violation` to `ErrConflict`.

The current v1 API has no idempotency keys. An uncertain transaction outcome
(transport failure or `503 commit_uncertain`) wraps `ErrUnavailable` and must
never replay the callback. Only a confirmed failed CAS may reload and retry.

**Serializability.** `memstore` gives every update serializable isolation over
the whole aggregate. `if_match` on changed documents only would allow write
skew. For example, two invitations could each count the members and pending
invitations, see room under `MaxMembers`, write different documents, and both
commit. So every commit that touches an organization's documents also patches
the organization root document with `if_match` (`inc /rev`), and every commit
that touches account-owned documents does the same on the user document. The
root documents act as the aggregate lock. With them, concurrent writers to one
organization serialize exactly as they do under the memstore lock.

**Limits.**
- A transaction holds at most 1024 ops, and no two ops may target the same
  document. The diff coalesces a document's changes into one op.
- A document holds at most 1 MiB, 4096 nodes and depth 16, so an organization
  cannot be one document. The collections below split it.
- Deleting an organization fans out to its projects, databases, keys,
  invitations and memberships. This stays well under 1024 ops at the plan
  limits: `MaxDatabases` is at most 100 and `MaxMembers` at most 100. A larger
  cascade needs a tombstone on the root and a background reaper.

**Document IDs.** TerraBase document IDs must be lowercase UUIDs. Dashboard
IDs (`usr_…`, `acct_…`, `org_…`, `prj_…`, `db_…`, `pat_…`, `inv_…`, `key_…`)
are not, so each document's ID is the SDK's
`uuidv5(uuidv5(NAMESPACE_URL, "terrabase:collection:" + name), dashboardID)`,
and the dashboard ID is kept in the body (`/id`). Natural-key IDs below use the
same helper with the named key.

**Encoding.**
- Times are RFC 3339 strings.
- Expiry and purge paths are RFC 3339 strings or Unix milliseconds.
- Counters are exact decimals.
- Empty strings that a unique constraint covers (`customer_id`,
  `subscription_id`, `slug`) must be omitted, not stored as `""`. TerraBase
  only skips missing or `null` paths, and every empty value would collide.

**Resource payloads.** Phase 1 retains `Database.Documents`, history, schema,
indexes, recommendations, builds and backups as Store-backed local behavior.
They persist with the database record. TODO(TS-463, TS-521): connect actual
resource operations after the producer and Management contracts land. Local
API-key secrets are dashboard records, not TerraBase data-plane credentials.
The embedded payloads remain subject to TerraBase's document-size limits.

**Current v1 capabilities.** There is no native unique-constraint, case-folding,
index or TTL configuration endpoint. The following table states the logical
constraints and query/expiry fields. Provisioning creates schemaless collections
only. Never send speculative schema/index/TTL settings.

Uniqueness uses normalized fields and deterministic `kind=unique_claim`
documents in the same collection as the entity. Claims commit in the entity's
transaction using create-only CAS; replacement/removal uses `if_match`.
`kind=record` distinguishes entities from claims. Email keys are normalized with
`core.NormalizeEmail`; scoped names use normalized keys. Plan ID/key
cross-uniqueness serializes on the catalog root. Claims never contain secrets.

Expiry is checked when resolving credentials, sessions, grants and throttle
buckets, and when counting live tokens/invitations/checkout references. Stale
physical records do not grant access. Native TTL and query indexes are future
producer features; until then queries use the paged Documents scan. Retention
metadata does not promise automatic physical deletion. Purging historical
records requires a separate maintenance command; this PR installs no reaper.

## Collections

All collections live in database `dashboard`. The table is the plan's table,
plus the indexes and paths the invariants below need. Changes from the plan
are in bold.

| Collection | ID | Logical uniqueness / query / expiry fields |
|---|---|---|
| `users` | uuidv5(account ID) | unique `lower(/email)`; **index `/created_at` (admin list)**; **prefix search on `lower(/email)`, `lower(/name)`** |
| `access_tokens` | uuidv5(`/token_hash`) | unique `/token_hash`; index `/user_id`; TTL `/expires_at`; cap of 25 per user, see invariant 4 |
| `sessions` | uuidv5(token digest) | unique hash (the ID); TTL `/expires_at`; **index (`/account_id`, `/user_id`)** |
| `device_grants` | uuidv5(device code digest) | **unique `/user_code_hash`**; **index `/client_key`**; TTL `/expires_at` (10m) |
| `login_throttle` | uuidv5(bucket key) | TTL `/due` |
| `organizations` | uuidv5(org ID) | unique (`/owner_user_id`, `/slug`); **index `/plan_id`**; root of the org aggregate (`/rev`, **`/db_count`**, **`/active_members`**) |
| `memberships` | uuidv5(org ID + ":" + user ID) | **index (`/user_id`, `/status`)**; **index (`/org_id`, `/status`)** |
| `invitations` | uuidv5(invitation ID) | unique `/token_hash`; pending unique through a deterministic ID; **index `/org_id`**; TTL **`/purge_at`** |
| `projects` | uuidv5(project ID) | unique (`/org_id`, `/name`); `/db_count`; **index `/org_id`** |
| `databases` | uuidv5(database ID) | unique (`/project_id`, `/name`); **index `/org_id`** |
| `api_keys` | uuidv5(key ID) | **index `/project_id`** |
| `billing` | uuidv5(org ID) | unique `/customer_id`, unique `/subscription_id` |
| `checkout_sessions` | uuidv5(session ID) | TTL **`/purge_at`**; **index (`/plan_id`, `/status`)**; **index `/org_id`** |
| `plans` | uuidv5(plan ID) | unique `/key`; **catalog lock document, see invariant 9** |
| `notifications` | uuidv5(notification ID) | index (`/user_id`, `/read`, `/created_at` desc) |
| `audit_events` | uuidv5(event ID) | index (`/org_id`, `/created_at` desc); TTL `/purge_at` (retention) |
| `admin_actions` | UUIDv7 | **index `/recorded_at`** |

Project sub-records have no collection in the plan. They are saved queries,
shared result configurations, backup policies and the ts-auth instance
(`AuthInstance` resources and audit events). So is `Account.DemoSecurity`. They
are small and bounded, so they can stay embedded in the `projects` and `users`
documents as long as those stay under the 4096-node limit. `AuthInstance`
resources are the one that can grow, and they should become a collection
(`auth_resources`, index `/project_id`) if they do.

## Invariants

Each item names what `memstore` does today under its lock and how the adapter
does it on TerraBase v1.

1. **Unique email** (`byEmail` index; `InsertAccount`, `ChangeEmailAs`).
   - *v1:* the `users` unique `lower(/email)`. `InsertAccount` is one
     transaction that creates the user, organization, membership, project and
     billing documents. `409 unique_violation` maps to `ErrConflict`.
   - `ChangeEmailAs` patches the user document with `if_match` on the version
     whose `password_hash` was compared. In the same transaction it patches
     the user's memberships, invariant 3.

2. **Account and organization IDs are new on insert** (`InsertAccount`
   conflict checks).
   - *v1:* `create` (`if_none_match:"*"`) on every document of the
     transaction.

3. **`syncMemberships`**: a user's name and email are copied into their member
   record in every organization, on every update.
   - *v1:* query `memberships` by `/user_id` (one index range, bounded by the
     user's organizations). Add a `patch` op per membership whose copy differs,
     in the same transaction as the user change. The query is paged; native secondary indexes are not available in this release.

4. **Access tokens** (`byAccessToken` index; at most `MaxAccessTokens` (25) live
   tokens; a credential change ends every token).
   - *Resolve:* `AccessTokenPrincipal` is a point `GET` of
     `uuidv5(token_hash)`, then the user document.
   - *Expiry:* expired tokens are rejected on resolve and pruned on the next issue,
     matching `memstore`. Physical deletion is separate maintenance.
   - *Cap:* the plan says "counter plus `assert`". That does not work with
     TTL, because a reaped token never decrements the counter. Instead, count
     live tokens with a query `count` on `/user_id`, then create the token in
     a transaction that also patches the user document with `if_match`.
     Concurrent issues serialize on the user version, and a 412 recounts.
   - *Credential change:* when `password_hash` changes, `Update` bumps
     `AuthVersion` and drops every token. Either delete the user's tokens (25
     at most) in the same transaction, or store `/auth_version` on each token
     and reject a mismatch when resolving.

5. **`repairSelectionsLocked`**: after every update, `memstore` scans every
   account. It drops organizations the account is no longer an active member
   of, moves a selection off a lost organization (to Personal first, else any
   other) and off a deleted project (to the organization's default project).
   - *v1:* do this lazily on read, not as a fan-out on write. The account's
     organizations come from querying `memberships` by (`/user_id`,
     `/status = active`), so a removed membership disappears without touching
     the other user's document.
   - Every read that resolves a principal applies the same fallback to the
     stored selection: `SnapshotAs`, `ViewAs`, `ScopedPrincipal`,
     `CurrentPrincipal`, `SessionPrincipal` and `AccessTokenPrincipal`. Make it
     a pure function in `core` that both stores use.
   - The adapter persists a repaired selection on the account's next write.
   - *Difference:* `memstore` picks "any other organization" in map order. The
     adapter must pick deterministically, for example the oldest active
     membership.

6. **`providerUniqueLocked`**: a Stripe customer ID and subscription ID belong
   to at most one organization. `memstore` scans every organization on every
   update and on `UpdateOrganization`.
   - *v1:* the `billing` unique `/customer_id` and `/subscription_id`. Omit
     empty values (see Encoding).

7. **`withinDatabaseLimitLocked`**: an organization may not gain databases
   past its plan's `MaxDatabases`. One already past it after a downgrade keeps
   them but cannot add more. The count is **per organization**, summed over
   its projects.
   - *v1:* the plan puts `db_count` on `projects`, but the limit is per
     organization. Keep `/db_count` on the **organization root** and patch it
     in the same transaction as each database create or delete, under the
     root's `if_match`.
   - Bind the plan value with `assert` on the plan document,
     `filter {"eq":["/max_databases",N]}`.
   - The project-level counter can stay for display.

8. **UpdateAs scope guard** (`reflect.DeepEqual` on every other organization):
   a scoped update may change only the principal's selected organization, plus
   new organizations the principal owns.
   - *v1:* a pure check on the diff from step 3. A changed, created or deleted
     document belonging to another organization is `ErrForbidden`. A new root
     must have `owner_user_id` = the principal with an owner membership. There
     is no global state.

9. **Plan catalog** (`UpsertPlanAs`: keys never change, and no plan's ID may
   equal another's key or the reverse; `Plan`/`Plans` read it).
   - *Does not map cleanly:* the unique `/key` covers key-to-key collisions,
     but an ID-to-key collision spans two paths of different documents.
   - The catalog is tiny and rarely written, so every plan write patches a
     `plans` catalog lock document with `if_match` after reading the whole
     collection (one query, at most 1000 rows).

10. **`DeletePlanAs` reference check**: delete the plan and CAS the catalog
    lock in one transaction containing two native `AssertCount` operations:
    no organizations with `/plan_id`, and no live open checkout references.
    Plan-binding transactions assert the plan's `/rev`, so a reference cannot
    race deletion. No retiring state or multi-step compensation is needed.

11. **Member limit** (`AcceptInvitation` counts active members against
    `MaxMembers`; `organization.CreateInvitation` also counts unexpired pending
    invitations).
    - *v1:* keep `/active_members` on the organization root and update it in
      every membership transaction under the root's `if_match`.
    - Bind the plan with `assert` filter `{"eq":["/max_members",N]}`.
    - Pending invitations are counted by query (`/org_id` index, filter on
      status and `expires_at`) under the same root `if_match`, which prevents
      write skew.

12. **Invitations by token** (`FindInvitation`, `AcceptInvitation` and
    `DeclineInvitation` scan every invitation of every organization).
    - *v1:* the unique `/token_hash`, so each call is one point lookup
      (`query eq /token_hash`, or ID = uuidv5(token hash)).
    - *Accept* is one transaction: patch the invitation (`if_match`), create
      the membership (`if_none_match:"*"`), patch the org root (`if_match`,
      `active_members`), patch the user's selection (`if_match`) and `assert`
      the plan.
    - *Pending uniqueness* per (organization, email) uses the plan's
      deterministic ID: a pending-slot document `uuidv5(org ID + ":" + email)`
      created with `if_none_match:"*"` and deleted when the invitation leaves
      `pending`.
    - *TTL:* `memstore` never deletes invitations. Expiry is a time check, and
      the organization pages show expired, declined and accepted ones. A TTL
      on `/expires_at` would hide them, so the TTL path is a separate
      `/purge_at` (expiry plus a retention period).

13. **Organization slug** (`CreateBusiness` rejects a second organization with
    the same slug owned by the caller).
    - *v1:* the unique (`/owner_user_id`, `/slug`).
    - *Behavior change to decide:* `RenameOrganization` updates the slug
      without that check today, so the constraint would turn a rename onto an
      existing owned name into `409`. Personal organizations have no slug and
      must omit it.

14. **Project and database names** (unique per organization and per project,
    checked inside the callbacks).
    - *v1:* the unique (`/org_id`, `/name`) and (`/project_id`, `/name`) as a
      second line of defense. The callback checks plus the root `if_match`
      already serialize them.

15. **`UpdateOrganization`** (Stripe sync; it receives the plan catalog).
    - *v1:* load the org root, the billing and checkout documents and the
      plans, apply, then commit with `if_match` on the root.
    - The `Billing.SyncedAt` ordering stays in the callback.

16. **Checkout sessions** keep a plan in use while open. A demo session stops
    counting at its own `ExpiresAt`; a provider session stops only at a
    terminal status.
    - *TTL:* set `/purge_at` only on demo or terminal sessions, never on open
      provider sessions.

17. **Update guard rails** (`updateLocked`): IDs and email are immutable
    through `Update`, the password hash is non-empty, a credential change bumps
    `AuthVersion`, and `AuthVersion` is otherwise preserved.
    - *v1:* a pure check on (loaded, mutated), shared by both stores.

18. **Admin reads** (`PlatformAccounts` copies every account; the admin pages
    page through them and search them).
    - *v1:* paged queries on `users` (sort `/created_at`, cursor, `count` for
      totals), and `prefix` on lowercased email and name for search.
    - The interface returns the whole list today; the adapter step should add
      paging to the interface rather than load every user.

19. **Notifications and audit events** are slices on `Account` today.
    - *v1:* their own collections, written in the same transaction as the
      change that records them.
    - *Audit retention:* the retention TTL needs an absolute `/purge_at`,
      computed from the organization's `AuditRetentionDays` when the event is
      written. Changing the retention does not move existing events; a
      background job must rewrite them if that is required.

20. **Admin action log** (`RecordPlatformAdminAction` appends).
    - *v1:* `create` with UUIDv7. Read with sort `/recorded_at`.

## Sessions, device grants and login throttle

- **`session.Store`** (`Put`, `Get`, `Delete`, `DeleteUser`, `ListUser`):
  - Documents are keyed by the token digest, never the token, with TTL
    `/expires_at`.
  - `DeleteUser` (`RevokeUser`) and `ListUser` need the
    (`/account_id`, `/user_id`) index, which the plan table lacks. A user's
    sessions are unbounded, so `DeleteUser` deletes in pages, and
    `SessionPrincipal`'s `AuthVersion` check already protects credential
    changes.
  - `Manager.principalForHash` preserves session-store and account-store
    errors. Missing or expired sessions and invalid credential versions resolve
    as absent. Ordinary browser and bearer wrappers and `BindCLIGrant` still
    return a `bool` and fail closed on store errors.
  - `CLIGrantPrincipal` returns validity plus an error, so device polling
    distinguishes an invalid session from an outage. Polling resolves the session
    before atomically claiming redemption, so a lookup outage leaves the approval
    unclaimed and needs no compensating write.
  - `Manager.End` returns deletion errors and attempts both distinct cookie
    and syntactically valid bearer tokens without first resolving a principal.
    It clears the browser cookie only after every deletion succeeds. Logout,
    invitation account switching, and password-change sign-out return 503
    instead of a successful redirect when deletion fails. A committed password
    change still invalidates the old credential version and reports that the
    password was updated even when sign-out cleanup fails.

- **`auth.DeviceGrants`**:
  - Grants are keyed by the device-code digest and looked up by the user-code
    digest (unique).
  - The TTL path is `/purge_at`, not `/expires_at`. A poll on an expired
    grant must still answer `expired_token`, not `invalid_grant`, so the grant
    must stay readable for a while after it expires.
  - Poll pacing (`next_poll`, `interval`), approval and the one-shot
    redemption are conditional updates (`if_match`), so two polls cannot both
    redeem.
  - The approving browser session is stored as its session digest, which is
    re-resolved through `session.Store` at redemption, not as a closure.
  - The per-client cap (10) is a query `count` on `/client_key`.
  - The global cap (1000) is approximate without a counter document; the
    adapter may drop it in favor of the per-client cap and TTL.

- **`throttle.Store`** (`Reserve`, `Refund`, `Reset`):
  - Each GCRA bucket is a document `uuidv5(bucket key)` with TTL `/due`.
  - `Reserve` reads both buckets (client and account) and commits one
    transaction with `if_match` (or `if_none_match:"*"`) on each, retrying on
    412.
  - The store enforces logical bucket expiry; physical expiry needs
    future native TTL or explicit maintenance.
  - The limiter refuses an attempt when the store fails, so an outage never
    disables the limit.

## Bootstrap and operational limits

`New(db *v1.Database) *Store` performs no I/O and keeps no hydrated state.
Every call loads documents into an isolated `memstore.FromState` validation
workspace and commits its diff with root revision guards. `EnsureCollections`
and `SeedPlans(ctx)` are explicit provisioning/test-fixture operations. Neither
application startup nor readiness seeds state.

`TERRABASE_URL`, `TERRABASE_API_KEY`, `TERRABASE_PROJECT_ID` and
`TERRABASE_DATABASE` select this backend together; unset selects memstore.
Readiness reads `users` with a one-row limit and reports the backend. See
[terrabase-cutover.md](terrabase-cutover.md) for the COS-run production steps.

Transactions are limited to 1024 operations and document bodies to 1 MiB,
4096 nodes and depth 16. Oversized mutations fail before partial publication.
Native `AssertCount` scans at most 10,000 entries; exceeding the producer's
limit fails safely. No native index, TTL, Management or Rust API endpoint is
called for phase-1 persistence.
