---
name: sqlite-d1-to-terrascale
description: Migrate SQLite files and Cloudflare D1 databases to TerraScale, including Worker SQL calls and recovery planning. Use when asked to import a .db or .sqlite file, migrate from D1, or move a Cloudflare Worker database to TerraScale.
---

# Migrate SQLite files or Cloudflare D1

Choose the source-specific export below, then follow the common import,
verification, cutover and rollback sequence. Read the
[feature mapping and Worker example](references/worker-and-features.md) before
replacing D1 bindings or making schema decisions.

## Prerequisites

- For a file: `sqlite3`, read access to the correct database, and a way to stop
  every writer. Use the [SQLite CLI](https://sqlite.org/cli.html).
- For D1: access to the correct Cloudflare account/environment, the application’s
  Wrangler configuration, and [Wrangler](https://developers.cloudflare.com/d1/wrangler-commands/).
- A destination project, an empty database, and an API key scoped to it; follow
  [getting started](https://docs.terrascale.tech/getting-started/) and install the
  [TerraScale CLI](https://docs.terrascale.tech/cli).
- Protected working storage, a maintenance window and a recoverable source.
  SQL artifacts contain application data; do not commit them or print secrets.

TerraScale SQL uses the SQLite dialect, with documented restrictions. A familiar
file extension or source API does not establish feature compatibility; see
[SQL](https://docs.terrascale.tech/concepts/sql).

## 1. Inventory the file or D1 database

- [ ] Identify the authoritative file path or remote D1 database, its size,
  schema revision, environment and application owner.
- [ ] List tables, indexes, views, triggers, foreign keys, row counts and primary
  keys. Find virtual tables, extension functions, generated columns, large blobs,
  integer boundaries and source-only pragmas before exporting.
- [ ] For a file, record all processes and scheduled jobs that write to it. Do not
  copy a live database file without its consistency requirements; use `.dump`
  against the database through `sqlite3` after freezing writers.
- [ ] For D1, record database IDs, binding names such as `DB`, preview/local/remote
  distinctions and every Worker/queue/cron/admin writer. Inspect remote metadata
  and schema with the documented
  [Wrangler commands](https://developers.cloudflare.com/d1/wrangler-commands/):

```bash
npx wrangler d1 list
npx wrangler d1 info "$D1_DATABASE"
npx wrangler d1 execute "$D1_DATABASE" --remote --command "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name"
```

- [ ] Use the appropriate `--env`/`--config` consistently when the application has
  several environments. Do not accidentally migrate Wrangler’s local dev copy.
- [ ] Find `.prepare().bind().all()`, `.first()`, `.run()`, `.batch()`, raw `exec`,
  sessions/bookmarks, migration tracking and code consuming D1 result metadata.
  Sources: [D1 database API](https://developers.cloudflare.com/d1/worker-api/d1-database/)
  and [prepared statements](https://developers.cloudflare.com/d1/worker-api/prepared-statements/).
- [ ] Record a source recovery point. For D1, inspect
  `npx wrangler d1 time-travel info "$D1_DATABASE"`; see
  [Time Travel](https://developers.cloudflare.com/d1/reference/time-travel/).

## 2. Plan the destination and downtime

- [ ] Create a new, empty destination in the intended project using the
  [CLI](https://docs.terrascale.tech/cli):

```bash
ts auth login
ts -project "$TERRASCALE_PROJECT_ID" db create "$TERRASCALE_DATABASE_NAME"
```

- [ ] Review schema against [SQL support](https://docs.terrascale.tech/concepts/sql):
  foreign keys are always enforced; `ATTACH`, virtual/temporary tables and many
  pragmas are refused. Do not import source service metadata as application data.
- [ ] Plan parent-before-child data loads and create side-effect triggers only
  after rows are loaded if replay would duplicate their effects. For cycles,
  rehearse an explicit staging/schema migration; do not rely on turning off
  destination foreign-key enforcement.
- [ ] Choose a write freeze long enough for export, conversion, import and checks.
  TerraScale has no live link, local-file replica, continuous source sync, or hosted
  migration service in its [import guide](https://docs.terrascale.tech/guides/import).
- [ ] Check [request, storage and database limits](https://docs.terrascale.tech/limits),
  including space for a rehearsal or restored copy. Decide how oversized source
  rows will be transformed before the production migration.
- [ ] Preserve a source copy and application configuration for rollback. Resolve
  all blocking feature-table rows before scheduling the final freeze.

## 3. Export the chosen source

- [ ] Rehearse using representative data; then freeze every source writer and
  drain accepted jobs before producing the final artifact.
- [ ] Set restrictive permissions and retain an immutable original export:

```bash
umask 077
mkdir -p "$MIGRATION_DIR"
```

### Plain SQLite file

[SQLite `.dump`](https://sqlite.org/cli.html#converting_an_entire_database_to_a_text_file)
exports schema and data as SQL:

```bash
sqlite3 app.db .dump > "$MIGRATION_DIR/original.sql"
```

Use the actual authoritative path in place of `app.db`. Wait for successful exit
before preparing/importing; treat an interrupted or failed export as invalid.
The [public import guide](https://docs.terrascale.tech/guides/import) shows a raw
`.dump` pipeline, but do not execute it with the reviewed CLI: its importer passes
transaction wrappers unchanged and splits trigger bodies. Prepare the artifact
in step 4 instead; the reference documents the implementation discrepancy.
TerraScale does not open, attach, or replicate a local file, per the import guide.

### Cloudflare D1

Export the remote database with
[Wrangler `d1 export`](https://developers.cloudflare.com/d1/wrangler-commands/#d1-export):

```bash
npx wrangler d1 export "$D1_DATABASE" --remote --output "$MIGRATION_DIR/original.sql"
```

Keep schema and data in the initial export. `--no-data`, `--no-schema`, and
`--table` select partial exports; use them only with a separately verified load
plan. Check the command exit status and preserve the original artifact.

## 4. Convert and import SQL

- [ ] Preserve `original.sql`. Follow the required
  [artifact preparation](references/worker-and-features.md#prepare-the-cli-artifact)
  to produce `source.sql`: remove complete top-level dump transaction controls
  and pragmas, and extract complete trigger DDL into separate reviewed files.
  This applies to both file dumps and D1 exports; the CLI does not normalize them.
- [ ] Review the prepared artifact for unsupported SQL, extension objects, service
  bookkeeping and dependency ordering. Parse complete SQL statements, including
  trigger bodies and quoted semicolons; do not split on semicolon characters.
- [ ] Use the [CLI importer](https://docs.terrascale.tech/guides/import) only for
  the prepared, trigger-free artifact; it handles batching:

```bash
ts -project "$TERRASCALE_PROJECT_ID" db import "$TERRASCALE_DATABASE_NAME" "$MIGRATION_DIR/source.sql"
```

- [ ] Verify loaded rows before installing each reviewed trigger as one complete
  HTTP statement using the reference example. Then verify trigger definitions
  and side effects. Never pass trigger files to `ts db import`.
- [ ] Import is not one atomic transaction. Earlier batches remain committed on
  failure. Keep the exact artifact and the reported first statement of the failed
  batch; resume with `-from <n>` only after confirming that batch did not commit.
  For uncertain outcomes reconcile first, or use a fresh target. See
  [import](https://docs.terrascale.tech/guides/import).
- [ ] For a custom loader, create tables first, then submit bound row inserts in
  batches. Stay within 256 statements, 16 KiB per statement, 1 MiB request body,
  1,024 changed rows and 8 MiB growth per transaction, accounting for triggers.
  A single oversized row needs a deliberate schema/data redesign; smaller batches
  cannot fix it. See [batch import](https://docs.terrascale.tech/guides/import)
  and [limits](https://docs.terrascale.tech/limits).
- [ ] Do not send dump transaction wrappers as raw HTTP statements. An HTTP request
  is its own transaction; `BEGIN`, `COMMIT` and `ROLLBACK` are refused. Neither
  the current CLI nor HTTP strips these controls; remove dump wrappers before
  constructing actual statement batches following
  [SQL over HTTP](https://docs.terrascale.tech/guides/sql-http).

## 5. Replace application access and recovery

- [ ] For a local-file application, replace file reads/writes with documented
  [SDK](https://docs.terrascale.tech/guides/sdks) or
  [HTTP](https://docs.terrascale.tech/guides/sql-http) operations. Revisit offline
  assumptions and network-failure behavior; there is no local replica path.
- [ ] For a Worker, replace the binding operations with the
  [before/after example](references/worker-and-features.md#worker-before-and-after).
  Preserve authorization before each database operation and normalize result shapes.
- [ ] Put `TERRASCALE_API_KEY` in a Worker secret; project/database IDs are routing
  configuration. Configure secrets for the chosen environment, never `vars` or
  browser code. See [Worker secrets](https://developers.cloudflare.com/workers/configuration/secrets/).
- [ ] A D1 binding is replaced by an authenticated HTTP call. Exchange the API key
  for an access token, refresh before expiry, and never use the API key itself as
  the SQL bearer token. See [authentication](https://docs.terrascale.tech/guides/authentication).
- [ ] Replace D1 Time Travel procedures with destination snapshot/PITR procedures.
  D1 bookmarks/history do not transfer through SQL. Destination restoration can
  create a new database; an in-place rewind discards later changes and needs
  explicit consent. Use [backups and restore](https://docs.terrascale.tech/guides/backups-and-restore)
  and the reference comparison; do not promise the same retention window.

## 6. Verify before switching

- [ ] Compare exact counts, primary-key sets, foreign-key relationships, indexes,
  views, trigger behavior, nulls, blobs and numeric boundaries with the frozen source.
- [ ] Compare canonical row hashes in stable-key pages; do not truncate to the
  destination [result limits](https://docs.terrascale.tech/limits).
- [ ] Run representative queries and `PRAGMA foreign_key_check`, a supported
  inspection pragma in [SQL](https://docs.terrascale.tech/concepts/sql).
- [ ] Exercise create/read/update/delete, a failed batch that rolls back, missing
  credentials, insufficient scope, and application input validation.
- [ ] For Workers, verify the response contract expected by callers, auth exchange,
  remote errors, bounded result sets and the deployed environment’s secret binding.
- [ ] Record export checksum, schema revision, counts, target identity and test
  evidence. Rehearse a destination restore into a separate database and validate
  actual restored rows using [backups and restore](https://docs.terrascale.tech/guides/backups-and-restore).

## 7. Cut over

- [ ] Keep the source write-frozen; if rehearsal data is stale, perform the final
  export/import/verification into a fresh empty target.
- [ ] Deploy the rewritten application with destination routing and least-privilege
  credentials, then verify a canary read and write through the real application.
- [ ] Release traffic and jobs while observing failures, latency and business totals.
  Stop rollout if accepted operations cannot be reconciled.
- [ ] Retain the source, D1 binding configuration and artifacts for the agreed
  rollback window. Remove obsolete bindings/credentials only after acceptance.

## 8. Roll back

- [ ] Before destination writes, switch the application back to its original file
  or D1 binding/configuration and resume source writers.
- [ ] After destination writes, freeze again and capture the destination’s accepted
  changes. Reconcile those operations into the source before switching back;
  pointing at the old source alone loses acknowledged writes.
- [ ] Use operation IDs to prevent duplicates; verify totals and key sets again.
  Keep one writable authority throughout recovery.
- [ ] A D1 Time Travel restore repairs the source’s historical state; it does not
  ingest destination writes. Do not invoke it as a substitute for reconciliation.
  Follow the [D1 recovery procedure](https://developers.cloudflare.com/d1/reference/time-travel/)
  only for an explicitly chosen source recovery point.
