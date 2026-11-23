---
name: turso-to-terrascale
description: Migrate Turso and libSQL databases, per-tenant fleets, and client code to TerraScale. Use when asked to migrate from Turso, move off libSQL, or move any database like Turso into TerraScale.
---

# Migrate Turso to TerraScale

Use this for a single remote Turso database, a database-per-tenant fleet, or an
application using `@libsql/client`. Finish with a verified data copy, an application
using the destination, and a tested rollback boundary.

## Prerequisites

- Access to the correct Turso organization and every database being migrated;
  an authenticated [Turso CLI](https://docs.turso.tech/cli/auth/login).
- A TerraScale project, the [CLI](https://docs.terrascale.tech/cli), and application
  keys restricted to their destination databases, following
  [getting started](https://docs.terrascale.tech/getting-started/).
- Bash, protected disk space for dumps, and an application maintenance window.
  Keep SQL dumps out of Git; they contain customer data.
- Read [feature mapping and client changes](references/client-and-features.md)
  before choosing a destination schema or changing application code.

TerraScale describes its SQL as the SQLite dialect. This does not establish
compatibility with libSQL-specific functions or features; use the documented
[SQL support and refusals](https://docs.terrascale.tech/concepts/sql).

## 1. Inventory the source and tenants

- [ ] Confirm the Turso account/organization before listing databases.
- [ ] Collect the database fleet, groups, and available source locations:

```bash
turso db list
turso group list
turso db locations
turso db list --group "$TURSO_GROUP"
turso db show "$TURSO_DATABASE"
```

These commands are documented under [db list](https://docs.turso.tech/cli/db/list),
[group list](https://docs.turso.tech/cli/group/list),
[locations](https://docs.turso.tech/cli/db/locations), and
[db show](https://docs.turso.tech/cli/db/show). Repeat the group-filtered list for
all groups; do not assume the first group contains the entire fleet.

- [ ] Record tenant ID, source name/ID, group, location, size, schema version,
  application owner, and whether the database is production or a branch.
- [ ] Inventory tokens by scope and consuming service, without recording values.
  Find `TURSO_DATABASE_URL`, `TURSO_AUTH_TOKEN`, `syncUrl`, `syncInterval`,
  `client.sync()`, transactions, vector functions, and `ATTACH` in application code.
- [ ] Inventory tables, row counts, foreign keys, indexes, views, triggers, blobs,
  large integers, generated IDs, and any virtual tables or extension-dependent SQL.
- [ ] Identify every writer: web requests, workers, queues, cron jobs, admin tools,
  and clients with pending local changes. Record how each will be stopped.

## 2. Plan destination databases and feature changes

- [ ] Keep one destination per tenant when isolation requires it. TerraScale
  recommends a separately scoped database per customer; databases share the
  organization allowance. See [scope application data](https://docs.terrascale.tech/blog/database-per-customer).
- [ ] Build an explicit source-to-destination manifest rather than deriving tenant
  identity from a URL or trusting an end-user-supplied database name.
- [ ] Check fleet size plus temporary copies against
  [database, storage, and request allowances](https://docs.terrascale.tech/limits).
  Map source locations to documented destination regions, measuring application
  latency rather than assuming Turso location codes transfer; see
  [SQL placement](https://docs.terrascale.tech/concepts/sql).
- [ ] Create empty destination databases in the intended project. Select that
  project explicitly; the CLI supports the following
  [project selection](https://docs.terrascale.tech/cli):

```bash
ts auth login
ts -project "$TERRASCALE_PROJECT_ID" db create "$TERRASCALE_DATABASE_NAME"
```

- [ ] Resolve every incompatible schema object before the production copy.
  `ATTACH`, virtual tables and unsupported pragmas cannot be carried across;
  foreign keys are always enforced. Plan parent-first loads and deferred creation
  of triggers that would duplicate imported side effects. See
  [SQL](https://docs.terrascale.tech/concepts/sql) and the reference feature table.
- [ ] Choose a write-freeze migration. TerraScale documents a dump-and-load import,
  without source replication or continuous sync. If the window is too long,
  design and verify an application change journal separately before proceeding;
  see [import](https://docs.terrascale.tech/guides/import).

## 3. Export and import each database

- [ ] Rehearse one representative tenant, including the largest row and a database
  with foreign keys. Measure the full copy and verification window.
- [ ] Freeze source schema and writes, drain jobs and pending local work, then
  record the last accepted operation and source counts.
- [ ] Export the remote database using the documented
  [Turso dump command](https://docs.turso.tech/cli/db/shell):

```bash
umask 077
mkdir -p "$MIGRATION_DIR"
turso db shell "$TURSO_DATABASE" .dump > "$MIGRATION_DIR/original.sql"
```

- [ ] Preserve `original.sql`. Follow the required
  [artifact preparation](references/client-and-features.md#prepare-the-cli-artifact)
  to produce `source.sql`: remove complete top-level dump transaction controls and
  pragmas, and keep complete trigger DDL in separate reviewed files. The current
  CLI does not normalize wrappers or preserve trigger bodies; the reference
  explains the discrepancy with the public import guide.
- [ ] Resolve extension functions, internal metadata and foreign-key load ordering
  in the prepared artifact. Test transformed values against the source.
- [ ] Import only the prepared, trigger-free artifact into the empty target using
  [batched import](https://docs.terrascale.tech/guides/import):

```bash
ts -project "$TERRASCALE_PROJECT_ID" db import "$TERRASCALE_DATABASE_NAME" "$MIGRATION_DIR/source.sql"
```

- [ ] Verify loaded rows before installing each complete reviewed trigger through
  the single-statement SDK path in the reference. Check trigger definitions and
  side effects afterward; never pass trigger files to `ts db import`.
- [ ] For a fleet, use the reference’s separate export and prepared-import loops.
  Prepare and rehearse every tenant’s artifacts, including dump controls and
  trigger separation, before starting the final import pass.
- [ ] The CLI commits batches independently. Save its failed-batch statement
  number and the exact input artifact. Resume with `-from <n>` only after
  confirming the failed batch did not commit; otherwise reconcile the target or
  restart into a fresh empty target. Never rerun a whole dump over partial data.
  This resume contract is documented in [import](https://docs.terrascale.tech/guides/import).
- [ ] For a custom loader, bind one row per `INSERT` and stay within 256 statements,
  16 KiB per statement, 1 MiB per body, 1,024 changed rows and 8 MiB growth per
  transaction. Include trigger effects in the budget. See
  [batch loading](https://docs.terrascale.tech/guides/import) and
  [limits](https://docs.terrascale.tech/limits).

Do not substitute `turso db export` blindly: its documented snapshot may omit
recent changes and needs SDK synchronization. Use the remote `.dump` path above
for this workflow; see [Turso export](https://docs.turso.tech/cli/db/export).

## 4. Replace clients, keys, and branches

- [ ] Apply the before/after SDK example and version prerequisite in
  [client changes](references/client-and-features.md#client-before-and-after).
- [ ] Replace source URLs/tokens with `TERRASCALE_PROJECT_ID`, the tenant’s mapped
  `TERRASCALE_DATABASE_NAME`, and a destination `TERRASCALE_API_KEY` from a secret
  manager. Never reuse a Turso token as a TerraScale credential. Destination keys
  are exchanged for short-lived tokens; see
  [authentication](https://docs.terrascale.tech/guides/authentication).
- [ ] Separate migration lifecycle permissions from runtime SQL permissions.
  Give each service only its database and required scopes; for dynamic issuance,
  use documented [scoped keys](https://docs.terrascale.tech/guides/agent-databases).
- [ ] Replace embedded replicas with remote queries or an explicitly designed
  application cache. Do not leave `.sync()` calls that imply a destination replica;
  [import](https://docs.terrascale.tech/guides/import) documents no local-file replication.
- [ ] Import divergent source branches separately if their data must survive.
  Create future destination previews from the migrated parent using
  [branches](https://docs.terrascale.tech/guides/branches):

```bash
ts -project "$TERRASCALE_PROJECT_ID" db branch create migration-preview --from "$TERRASCALE_DATABASE_NAME" --ttl 7d
```

Destination branches are independent snapshots, consume database slots, and expire
at their TTL. Apply reviewed schema migrations to production separately; do not
expect branch writes to merge. See [branches](https://docs.terrascale.tech/guides/branches).

## 5. Verify the whole fleet

- [ ] Compare schema objects, exact counts per table, primary-key sets, nulls,
  text encodings, blobs, numeric boundaries, and canonical row checksums.
  Page comparisons by a stable key; destination result limits are documented in
  [limits](https://docs.terrascale.tech/limits).
- [ ] Run representative reads and writes, uniqueness and foreign-key failures,
  trigger side effects, transaction rollback, and named/positional parameters.
- [ ] Run `PRAGMA foreign_key_check` on the target; the supported inspection
  pragmas are listed in [SQL](https://docs.terrascale.tech/concepts/sql).
- [ ] Verify tenant A cannot read tenant B using A’s runtime key; test missing and
  revoked credentials, queue processing, and source-specific feature replacements.
- [ ] Mark every manifest row verified with counts, artifact checksum, destination
  identity, copy time, and application smoke-test evidence. A successful import
  exit alone does not establish a complete migration.

## 6. Cut over

- [ ] With writers still frozen, take the final verified dump and repeat the
  rehearsed load into clean targets if the rehearsal copy is stale.
- [ ] Deploy the mapped destination configuration and keys; release a canary tenant
  first. Check latency, error rates, counts and a write/read round trip.
- [ ] Switch tenant routing only after its verification passes. Keep the source
  write-frozen and retain dumps through the rollback window.
- [ ] Configure destination backups and demonstrate restore to a new database;
  source recovery history does not migrate with SQL. See
  [backups and restore](https://docs.terrascale.tech/guides/backups-and-restore).

## 7. Roll back

- [ ] Before destination writes: restore source routing and credentials, then
  unfreeze the source writers. Leave failed targets isolated for diagnosis.
- [ ] After destination writes: freeze writers again, preserve destination data,
  and reconcile every accepted destination operation back to the source before
  restoring routing. A stale source is not a lossless rollback.
- [ ] Reconcile once with operation IDs and business-level totals. Do not enable
  two independent writers while deciding which copy is authoritative.
- [ ] Retain the original source and artifact until the agreed window ends; revoke
  obsolete source tokens only after acceptance. Record unresolved product gaps
  and their application workarounds in the migration handoff.
