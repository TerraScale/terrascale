---
name: supabase-client-to-terrascale
description: Rewrite Supabase client integrations to TerraScale SQL and documented ORMs. Use when migrating from Supabase, replacing supabase-js or supabase-py query builders, or moving Supabase auth, storage, functions and realtime calls in a Next.js, Python or mobile app.
---

# Rewrite Supabase application clients

Use this skill for application code after the destination schema and identity mapping
have been planned. Produce file-by-file rewrites, an operation inventory and a list
of behavior gaps. Database copying belongs to supabase-database-to-terrascale;
this skill does not infer that data or policies have already moved.

Read [TypeScript](references/supabase-js.md) for JavaScript/Next.js and
[Python](references/supabase-py.md) for sync or async Python. Each reference uses
Supabase before examples and TerraScale after examples with explicit result shaping.

## Prerequisites

- [ ] Identify the app framework, server/browser boundary, installed Supabase SDK
  versions, schema, constraints, policies, database functions and authenticated roles.
- [ ] Have a destination project and a migrated schema in the
  [SQLite dialect](https://docs.terrascale.tech/concepts/sql). Do not copy PostgreSQL
  functions, casts, array operators or schema prefixes into SQL unchanged.
- [ ] Check the current [JavaScript quickstart](https://docs.terrascale.tech/getting-started/javascript)
  and [Python quickstart](https://docs.terrascale.tech/getting-started/python).
  Their current APIs require source installation: the JavaScript docs distinguish
  the previous npm package from current source; Python publication to PyPI is pending.
  Record the source revision used and pin the app dependency to that reviewed revision.
- [ ] Provide TERRASCALE_PROJECT_ID, TERRASCALE_DATABASE_NAME and server-only
  TERRASCALE_API_KEY through the app secret store. SQL reads need sql:read;
  writes also need sql:write ([SQL HTTP](https://docs.terrascale.tech/guides/sql-http)).
  Never display values or put privileged keys into browser/mobile builds.

## 1. Inventory every call site

Start with tracked source and manifests, then include ignored application files
if the app keeps runtime code there. Exclude dependency, build and generated files.
Run searches from the app root; inspect matches and follow client aliases, wrappers,
re-exports, dependency injection and shared repository classes to their callers.

~~~sh
rg -n -i 'supabase|postgrest' --glob '!*-lock.*' --glob '!*.lock'
rg -n 'createClient|createBrowserClient|createServerClient|create_client|acreate_client|SupabaseClient|Supabase\.instance|supabase_flutter|supabase-kt'
rg -n '\.from\s*\(|\.table\s*\(|\.rpc\s*\(|\.auth\b|\.storage\b|\.functions\b|\.channel\s*\('
rg -n 'postgres_changes|postgresChanges|postgresChangeFlow|onPostgresChanges|broadcast|presence|onAuthStateChange|on_auth_state_change|authStateChanges'
rg -n 'from<|postgrest\[|decodeList|decodeSingle|\.execute\s*\(|\.subscribe\s*\(|\.stream\s*\('
rg -n 'SUPABASE_|NEXT_PUBLIC_SUPABASE|VITE_SUPABASE|PUBLIC_SUPABASE|SUPABASE_URL|SUPABASE_KEY'
~~~

The broad member search covers functions.invoke even across line breaks.
Python also uses table(...).execute() and async clients; Flutter uses
Supabase.instance.client.from(...), stream(...) and onPostgresChanges(...);
Swift uses SupabaseClient, from(...).select().execute() and channel APIs;
Kotlin uses createSupabaseClient, from(...), postgrest[...], rpc and
postgresChangeFlow. Search imports and the exact installed SDK API too:
[JS](https://supabase.com/docs/reference/javascript/introduction),
[Python](https://supabase.com/docs/reference/python/introduction),
[Flutter](https://supabase.com/docs/reference/dart/introduction),
[Swift](https://supabase.com/docs/reference/swift/introduction),
[Kotlin](https://supabase.com/docs/reference/kotlin/introduction).

- [ ] Record each match as: file, operation, arguments, return shape, caller,
  authorization, pagination, error handling, transaction needs and replacement.
- [ ] Include background jobs, API routes, server actions, middleware, hooks,
  subscriptions, optimistic caches and generated database types.
- [ ] Record RLS-dependent reads and writes; a server key does not automatically
  reproduce Supabase user policies. Identify the authenticated owner/tenant filter.
- [ ] Inspect RPC definitions, not just call names: record side effects, privileges,
  defaults, return types and atomicity. Mark extension-dependent operations as gaps.

## 2. Choose the target and preserve the boundary

Prefer a server data-access module using parameterized SQL when replacing isolated
query builders. Keep an existing supported ORM when it already owns the app schema.

| App | Documented target | Migration constraint |
| --- | --- | --- |
| JS/TS SQL | createTerraBaseV1Client(...).database(...) | Use the source version in the JS quickstart. |
| Drizzle | [Drizzle adapter](https://docs.terrascale.tech/guides/orm-drizzle) | Use its documented dialect; atomic multi-statement work uses batch. |
| Prisma | [Prisma adapter](https://docs.terrascale.tech/guides/orm-prisma) | Adapter needs source installation; migration commands have documented restrictions. |
| Python SQL | TerraScale / AsyncTerraScale | Use the source SDK; preserve async request handlers. |
| SQLAlchemy | [SQLAlchemy dialect](https://docs.terrascale.tech/guides/orm-sqlalchemy) | Review dialect, Alembic and transaction limits before porting models. |

The SDK entry points and availability are described in
[SDKs](https://docs.terrascale.tech/guides/sdks) and the language quickstarts.
Do not mix the previous JavaScript package examples with current source methods.

For a browser-first Next.js app, move privileged SQL into authenticated server
routes/actions or server components. Keep the client UI calling that app API.
Validate the session, derive the owner/tenant on the server, validate input and
include authorization in every query and mutation. Never accept arbitrary SQL,
a claimed owner ID or an unrestricted table name from a browser.

Direct end-user reads need scoped tokens and destination rules; a client-side
filter is not authorization. Follow
[end-user authentication](https://docs.terrascale.tech/guides/end-user-authentication)
and [live shapes and rules](https://docs.terrascale.tech/concepts/sql#live-shapes-and-rules).

### Mobile SDKs

The [official SDK list](https://docs.terrascale.tech/guides/sdks) does not document
native Dart/Flutter, Swift or Kotlin SDKs. Do not invent native TerraScale clients.
Keep mobile code calling an authenticated app backend that uses a documented SDK;
that backend returns the app DTOs formerly constructed from Supabase responses.
[SQL HTTP](https://docs.terrascale.tech/guides/sql-http) is a documented transport,
but is not a documented native mobile SDK or permission to ship a project API key.
Use the project's OIDC authorization-code/PKCE flow for mobile sign-in as described
in [end-user authentication](https://docs.terrascale.tech/guides/end-user-authentication).

## 3. Rewrite SQL operations file by file

- [ ] Replace client construction once in each server runtime; update imports and
  call sites together. Avoid leaving a wrapper that still calls Supabase.
- [ ] Translate select() to explicit SQL projections and bound filters. Map
  nullable values, booleans, JSON, exact integers and date serialization deliberately.
- [ ] Replace embedded relationship selections with joins and explicit nested DTO
  assembly; preserve missing parents, empty child arrays and !inner semantics.
- [ ] Replace insert/upsert/update/delete with parameterized statements. Preserve
  returned rows with RETURNING, conflict targets and affected-row checks.
- [ ] Translate inclusive range(start, end) to LIMIT end-start+1 OFFSET start;
  require a stable order with a unique tie-breaker. Use a separate COUNT(*) for
  exact counts. Estimated/planned counts have no mapping in these references.
- [ ] Replace single()/maybeSingle() with explicit cardinality checks.
  TerraScale query results are not Supabase { data, error, count } objects.
- [ ] Replace rpc() with its reviewed SQL equivalent or server application
  logic. Do not invoke PostgreSQL stored functions by name and hope they exist.
- [ ] Keep dependent writes atomic: an HTTP batch is one transaction; separate
  execute calls are separate transactions. HTTP refuses explicit transaction
  control ([SQL HTTP](https://docs.terrascale.tech/guides/sql-http)).
- [ ] Preserve domain errors while changing SDK exception handling. Do not retry
  uncertain writes blindly ([errors](https://docs.terrascale.tech/guides/errors)).

### Replace other Supabase services

Use one service migration handoff per family, retaining an entry in the call-site inventory:

- auth: replace Supabase session helpers with project OIDC discovery,
  authorization code with PKCE, validated callbacks, refresh and logout.
  Follow [end-user authentication](https://docs.terrascale.tech/guides/end-user-authentication);
  see also supabase-auth-to-terrascale for identity migration. Existing Supabase
  tokens are not destination credentials.
- storage: replace bucket/path calls with destination bucket/file IDs and
  signed transfers, retaining the mapping in app data. Files are immutable and
  public means authenticated project users, not anonymous access. Gate cutover
  on the documented rollout and live verification prerequisite in
  [file storage](https://docs.terrascale.tech/guides/file-storage);
  see also supabase-storage-to-terrascale for bulk objects and policies.
- functions.invoke: call the database-scoped
  POST /v1/projects/{project}/databases/{database}/functions/{name}/invoke
  from authorized server code with an exchanged access token; handle HTTP status
  and response JSON. Port the handler and its authorization under
  [functions](https://docs.terrascale.tech/guides/functions);
  see also supabase-edge-functions-to-terrascale. A Supabase function URL,
  Deno runtime assumptions and user JWT are not drop-in replacements.

### Realtime: split the three use cases

[Live shapes](https://docs.terrascale.tech/concepts/sql#live-shapes-and-rules)
send a table snapshot followed by committed changes, with filters and column
selection. The documented framework integrations require the next JS release;
use the source prerequisite before adopting them. They represent live row state,
not a promise to replay every Supabase event payload or ordered business event.

| Supabase use | TerraScale path or concrete workaround |
| --- | --- |
| postgres_changes to refresh a table UI | Use a table shape and rebuild the view from its rows; TS example is in the reference. Recompute joins through the app backend. |
| postgres_changes used as a durable event processor | Write an application outbox alongside the mutation in one batch; let a backend worker deliver with IDs, deduplication and a durable cursor. Shapes are not that contract. |
| Broadcast messages, typing indicators | No equivalent broadcast service is documented. Run an authenticated application WebSocket/SSE gateway or keep a dedicated messaging provider; define rooms, authorization and reconnect behavior there. |
| Presence, join/leave, online users | No equivalent presence service is documented. Keep presence in the application gateway with per-connection membership, heartbeat expiry and disconnect cleanup; expose its roster to clients. |
| LISTEN / NOTIFY | Refused with 0A000; do not use them as the gateway transport. Use an app outbox/worker or periodic authorized reads. |

The notifications restriction is explicit in
[Postgres clients](https://docs.terrascale.tech/guides/postgres-clients#catalog-queries-settings-and-notifications).
If source-only shapes cannot be adopted, periodically fetch an authorized, ordered
query through the app API with cancellation and a bounded refresh interval.
Document the freshness tradeoff; do not silently replace live UX with a one-time fetch.

## 4. Verify behavior before cutover

- [ ] Re-run the inventory and account for every match, including mobile wrappers.
- [ ] Run the app's normal typecheck/build; replace Supabase generated types with
  DTOs matching actual result columns and nullability.
- [ ] Against a migrated staging database, compare empty, one and many rows,
  all filters, tied ordering, page boundaries, joins with missing relations,
  counts and single() failures using the same fixtures on both systems.
- [ ] Verify insert/upsert conflicts, returned writes, no-match mutations,
  cross-tenant denial, unauthenticated denial and rollback of a failing batch.
- [ ] Exercise login/refresh/logout, file upload/download/denial, invoked function
  errors, shape reconnect/cleanup and gateway room authorization when applicable.
- [ ] Confirm browser bundles, mobile artifacts and logs contain no privileged
  credentials. Record only environment variable names and sanitized errors.
- [ ] Check payload and result sizes against [limits](https://docs.terrascale.tech/limits);
  split bulk writes at deliberate transaction boundaries.
- [ ] Attach a gap list to the migration review; block affected feature cutover
  until an implementation or explicit product decision covers each gap.

## 5. Cut over and roll back

- [ ] Deploy the rewritten server modules and client consumers as one compatible
  release after data synchronization and the service handoffs are verified.
- [ ] Replace SUPABASE_URL/SUPABASE_KEY/SUPABASE_SERVICE_ROLE_KEY in server
  configuration with destination variables. Remove NEXT_PUBLIC_SUPABASE_*,
  VITE_SUPABASE_* and PUBLIC_SUPABASE_* only after their consumers move.
  Keep public OIDC issuer/client-ID configuration separate from SQL credentials.
- [ ] Remove unused Supabase packages, helpers and env names after the last call
  moves. Inspect final dependency files and the inventory, not just imports.
- [ ] Retain the prior application release and a write-reconciliation plan for
  rollback. Pause writes or reconcile destination-only writes before restoring
  Supabase traffic; swapping environment variables cannot reconcile changed data.
- [ ] Validate the same critical flows after cutover. Restore the prior release
  if agreed error, authorization or data-consistency thresholds fail; record
  the write cutoff and reconcile before reopening writes.
