---
name: supabase-to-terrascale
description: Migrate a Supabase project to TerraScale by inventorying database, auth, storage, functions, realtime and client dependencies, then planning, rehearsing and cutting over safely. Use when asked to migrate from Supabase, move off Supabase, or migrate from Supa Base to TerraScale.
---

# Migrate a Supabase project

Use this entry point for a whole project or when the requested scope is unclear.
Produce an inventory and a filled migration plan before changing production.
For a database-only request, see also `supabase-database-to-terrascale`.
Each skill installs independently; install any needed sibling separately.

## Prerequisites

- [ ] Access to the app repository, deployment configuration and Supabase dashboard.
- [ ] Supabase CLI, PostgreSQL client tools, and an authenticated TerraScale CLI.
- [ ] An isolated rehearsal environment and a place to store private migration artifacts.
- [ ] A named cutover owner, rollback owner and agreed maintenance window.
- [ ] Secrets supplied by a secret manager; record environment variable names only.

Use the [Supabase CLI reference](https://supabase.com/docs/reference/cli/introduction)
and [TerraScale CLI installation guide](https://docs.terrascale.tech/cli).
Disable shell tracing and keep dumps, user exports, signed URLs and tokens out of
Git, chat, build logs and terminal output. Store data artifacts with restrictive
permissions; `umask 077` before creating the migration directory.

## 1. Inventory the source

Run from the application repository. Capture version and project metadata,
without reading or printing secret files:

```sh
supabase --version
supabase projects list
supabase functions list --help
```

Match the intended project reference against the dashboard before any export.
Use `supabase functions list --remote` on CLI versions that support `--remote`;
on older versions use the targeting option shown by installed help. The current
[functions list reference](https://supabase.com/docs/reference/cli/supabase-functions-list)
and [projects list reference](https://supabase.com/docs/reference/cli/supabase-projects-list)
describe these commands. Do not relink an existing checkout to a different project
without checking its current configuration.

Search filenames first so credentials embedded in source are never printed:

```sh
rg -l --hidden -g '!.git/**' -g '!node_modules/**' -g '!.env*' -g '!*lock*' \
  '@supabase/supabase-js|@supabase/ssr|supabase-py|from supabase import|create_client|createClient' .
rg -l -g '*.{js,jsx,ts,tsx,py}' \
  '\.from\(|\.table\(|\.rpc\(|\.schema\(|\.auth\.|\.storage\.|\.functions\.|\.channel\(|\.subscribe\(|postgres_changes|on_auth_state_change|onAuthStateChange' .
rg --files --hidden -g '!.git/**' -g '!node_modules/**' -g '!.env*' \
  | rg '(^|/)(supabase|migrations|functions)/|package\.json$|pyproject\.toml$|requirements[^/]*\.txt$'
```

Inspect each matching call site locally, including wrappers and aliases missed
by these patterns. Treat names like `createClient` as candidates, not proof.
The [JavaScript reference](https://supabase.com/docs/reference/javascript/introduction)
and [Python reference](https://supabase.com/docs/reference/python/introduction)
cover database, auth, storage, functions and realtime clients.

Fill one inventory row per dependency with its caller, owner, volume, permissions,
replacement, validation and status. Include:

- [ ] **Database:** app schemas, table sizes and exact counts, keys, SQL/RPC,
  views, triggers, RLS policies, extensions, scheduled jobs, migrations and backups.
- [ ] **Auth:** providers, password/email flows, MFA, users, sessions, callbacks,
  claims, service-role usage and foreign keys to `auth.users`.
- [ ] **Storage:** buckets, object counts/bytes, public access, ownership policies,
  signed URL lifetimes, upload transformations and cached URLs.
- [ ] **Functions:** deployed versions, Deno/npm dependencies, secrets by name,
  HTTP contracts, webhooks, schedules, retries and external side effects.
- [ ] **Realtime:** Postgres changes, broadcast, presence, subscriptions,
  disconnect behavior and permission filters.
- [ ] **Clients:** browser, server, SSR, mobile, Python jobs and third-party tools;
  SQL builders, RPC calls, generated types and old credentials.

In the dashboard inspect Database tables/extensions/policies, Authentication
providers/users/settings, Storage buckets/policies, Edge Functions deployments
and Realtime configuration. Compare deployed state with checked-in migrations;
files alone cannot establish production state. Use the
[Supabase database overview](https://supabase.com/docs/guides/database/overview),
[RLS guide](https://supabase.com/docs/guides/database/postgres/row-level-security)
and [Realtime overview](https://supabase.com/docs/guides/realtime).

## 2. Fill and approve the migration plan

Copy [the plan template and filled example](references/migration-plan.md) into a
private project document. Replace every placeholder and attach evidence for each
compatibility decision. Work through unresolved gaps proactively in rehearsal;
leave production blocked when a required capability has no verified replacement.

TerraScale SQL uses the SQLite dialect. A Postgres driver connection does not
make Postgres SQL, extensions or catalog-dependent ORMs compatible. Imports are
SQL batches, with no hosted migration service or source continuous sync:
[SQL](https://docs.terrascale.tech/concepts/sql),
[Postgres clients](https://docs.terrascale.tech/guides/postgres-clients),
[import](https://docs.terrascale.tech/guides/import).

Check these destination capabilities rather than promising parity:

- Authentication supports project OIDC sign-in; magic-link sign-in and guest
  accounts/conversion are not currently available. Use a supported sign-in flow
  or retain the existing identity provider behind your server. See
  [authentication capabilities](https://docs.terrascale.tech/guides/authentication-capabilities).
- End-user data access needs explicit authorization. Scoped data tokens and
  database rules cover shape/row reads; server-side authorization can protect
  application operations. Never replace Supabase RLS with a browser-held API key.
  See [data access for users](https://docs.terrascale.tech/guides/end-user-authentication#data-access-for-your-users).
- The file API has a rollout prerequisite and pending live verification in its
  guide. Confirm availability and exercise transfers and authorization for the
  project before committing to a storage cutover. See
  [file storage](https://docs.terrascale.tech/guides/file-storage).
- Functions are JavaScript handlers with SQL bindings and HTTP/cron execution;
  inventory dependencies before porting Deno code. See
  [functions](https://docs.terrascale.tech/guides/functions).
- SQL live shapes are documented, but do not assume they replace Supabase
  broadcast or presence. The SQL guide marks its framework examples as requiring
  the next JavaScript SDK release. Verify the installed release and your actual
  subscription contract. See [live shapes](https://docs.terrascale.tech/concepts/sql#live-shapes-and-rules).

## 3. Migrate and rehearse in dependency order

Use this implementation order; change routing only after the full rehearsal:

1. **Database:** translate schema/data, establish ownership columns, verify reads,
   writes and constraints. See also `supabase-database-to-terrascale`.
2. **Auth:** establish a stable source-user to destination-subject mapping;
   rewrite authorization and sign-in integration. See also
   `supabase-auth-to-terrascale`.
3. **Storage:** copy and reconcile objects and metadata; rewrite ownership and
   URLs against the identity map. See also `supabase-storage-to-terrascale`.
4. **Edge functions:** port runtime dependencies, secrets and schedules; keep
   external side effects disabled during rehearsal. See also
   `supabase-edge-functions-to-terrascale`.
5. **Client code:** replace queries, RPC, auth, uploads and subscriptions across
   every caller; regenerate types and configure deployment variables. See also
   `supabase-client-to-terrascale`.

Resolve circular dependencies in rehearsal: create app-owned identity mapping
structures before data import, then populate and verify mappings during auth
migration. Do not restore Supabase-managed `auth` or `storage` schemas as app data.

- [ ] Compare per-table counts and deterministic row samples at one write cutoff.
- [ ] Exercise sign-in, refresh, sign-out, two-user isolation and anonymous denial.
- [ ] Exercise upload/download and denial of another user's private objects.
- [ ] Run jobs and webhooks once against synthetic data; assert one side effect.
- [ ] Exercise all clients, reconnects and deployment rollback.
- [ ] Record actual copy duration and capacity; choose the final cutover option.

## 4. Choose and execute cutover

**Maintenance window** is the default when the system can stop writes. Pause all
writers, including jobs, webhooks and mobile/background clients; drain in-flight
work. Freeze schema and identity changes. Take the final export, run the tested
conversion/import, reconcile counts and samples, switch app configuration, run
smoke checks, then reopen writes. Keep the source available for rollback.

**Short dual-run** means a bounded app-controlled rehearsal or shadow period.
TerraScale has no continuous PostgreSQL sync; an independent branch is also a
snapshot, not a replication channel:
[import](https://docs.terrascale.tech/guides/import),
[branches](https://docs.terrascale.tech/guides/branches).
Keep Supabase authoritative initially. Shadow reads only after matching data
watermarks; suppress destination emails, payments and scheduled side effects.

If writes must continue, implement and verify an app-owned durable change log,
idempotent application, deletion handling, ordering and reconciliation first.
Uncoordinated writes to two databases are not a cutover strategy. Capture changes
before the initial snapshot and prove replay reaches the final write watermark.
If any writer bypasses capture, select a maintenance window instead.

At the final fence, pause writers, drain replay, compare at the same watermark,
switch routing once, and reopen. Avoid accepting independent writes on both sides.

- [ ] Change freeze, final backup and source cutoff recorded.
- [ ] All database, auth, files, jobs and client acceptance checks passed.
- [ ] Old workers disabled; destination workers enabled once.
- [ ] Deployment variables changed without printing values.
- [ ] Error rates, authorization denials and reconciliation checked after reopening.
- [ ] Rollback deadline, owner and handling of newly accepted writes recorded.

## 5. Roll back and close

If checks fail before destination writes open, retain the source routing, correct
the rehearsal artifacts, and repeat the final export at a new cutoff.

After destination writes open, fence all writers first. Export and reconcile new
rows, updates, deletes and identity/file changes back to the source through the
rehearsed reverse mapping before routing back. Do not merely restore an old
connection variable: that loses accepted writes. If reverse conversion is not
ready, remain in maintenance while recovering; honor the plan's RPO explicitly.

- [ ] Restore source routing and sessions as appropriate; verify two-user isolation.
- [ ] Restore one authoritative set of jobs and webhook consumers.
- [ ] Record incidents and reconciliation results without sensitive data.
- [ ] Keep source backups and migration artifacts through the agreed retention.
- [ ] Revoke old credentials and retire source resources only after acceptance
  and the rollback window; remove temporary replication/shadow mechanisms.
