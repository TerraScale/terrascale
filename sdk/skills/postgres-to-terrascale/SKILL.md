---
name: postgres-to-terrascale
description: Migrate a PostgreSQL database from Neon, AWS RDS or Aurora, Google Cloud SQL, Azure, or a self-hosted server to TerraScale. Use when asked to move off a Postgres host, convert pg_dump exports, replace PostgreSQL drivers or ORMs, or plan a verified database cutover and rollback.
---

# PostgreSQL to TerraScale

Use this for database migration from any PostgreSQL host. Inventory each database
separately; cluster roles, provider networking and backup history are separate
workstreams. Read [host connection notes](references/host-connections-and-recovery.md)
for the source in use, then the
[type and feature map](references/type-and-feature-mapping.md) before conversion.

TerraScale uses the SQLite dialect. Its Postgres wire protocol is a driver
transport with limited spelling translation, not a promise that PostgreSQL SQL,
extensions or ORMs work unchanged. There is no hosted migration service or live
replication from PostgreSQL; imports are converted SQL statements in batches.
Sources: [SQL](https://docs.terrascale.tech/concepts/sql),
[Postgres clients](https://docs.terrascale.tech/guides/postgres-clients),
[import](https://docs.terrascale.tech/guides/import).

## Prerequisites

- [ ] Authorized source read access to every selected table, schema and sequence;
  permission to inspect policies, definitions and dependencies. Arrange provider
  network access and authentication using the host notes.
- [ ] A supported `psql` and `pg_dump`; use the source major version or a newer
  supported client. `pg_dump` refuses a newer server major version. Read the
  [pg_dump reference](https://www.postgresql.org/docs/current/app-pgdump.html).
- [ ] A private migration directory outside Git, encrypted storage and enough disk
  for schema, INSERT dumps, converted rows and verification manifests. Dumps
  contain production data. Use `umask 077`; disable shell tracing and client logs
  that expose parameters, URLs, tokens or rows.
- [ ] Source credentials supplied through a protected libpq service/password file.
  Set `SOURCE_PGSERVICE`, and where needed `PGSERVICEFILE` and `PGPASSFILE`, through
  the secret manager. Never print the service file or a connection URL. See
  [service files](https://www.postgresql.org/docs/current/libpq-pgservice.html) and
  [password files](https://www.postgresql.org/docs/current/libpq-pgpass.html).
- [ ] A TerraScale project and empty target database created using
  [getting started](https://docs.terrascale.tech/getting-started/), with a
  server-held `TERRASCALE_API_KEY` scoped for the necessary database operations
  and SQL reads/writes. Install the documented [CLI](https://docs.terrascale.tech/cli).
  Set nonsecret `TARGET_DATABASE` and `TERRASCALE_PROJECT_ID` separately.
- [ ] An agreed write-freeze or application-managed change-capture plan, a tested
  rollback owner and enough source backup retention to cover the observation period.

Commands below run in the private directory. Example tables `public.customers`
and `public.orders` must be replaced with the inventoried names. Source catalog
queries run only against PostgreSQL, never against TerraScale.

## 1. Inventory the source and its traffic

Run the following through the source service. These use PostgreSQL's documented
[system catalogs](https://www.postgresql.org/docs/current/catalogs.html),
[information schema](https://www.postgresql.org/docs/current/information-schema.html)
and [size functions](https://www.postgresql.org/docs/current/functions-admin.html).

```sh
umask 077
PGSERVICE="$SOURCE_PGSERVICE" psql -X -v ON_ERROR_STOP=1 <<'SQL'
SELECT current_database(), current_setting('server_version');
SELECT nspname FROM pg_namespace
WHERE nspname NOT LIKE 'pg_%' AND nspname <> 'information_schema'
ORDER BY nspname;
SELECT extname, extversion, n.nspname
FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace
ORDER BY extname;
SELECT pg_database_size(current_database()) AS database_bytes;
SELECT n.nspname, c.relname, c.relkind, c.relispartition,
       c.reltuples::bigint AS estimated_rows,
       pg_total_relation_size(c.oid) AS total_bytes
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p', 'm')
  AND n.nspname NOT LIKE 'pg_%' AND n.nspname <> 'information_schema'
ORDER BY total_bytes DESC;
SELECT table_schema, table_name, column_name, data_type, udt_name,
       is_nullable, column_default, is_identity, is_generated
FROM information_schema.columns
WHERE table_schema NOT LIKE 'pg_%' AND table_schema <> 'information_schema'
ORDER BY table_schema, table_name, ordinal_position;
SELECT rolname, rolsuper, rolinherit, rolcreaterole, rolcreatedb,
       rolcanlogin, rolreplication, rolbypassrls FROM pg_roles ORDER BY rolname;
SELECT parent.rolname AS granted_role, member.rolname AS member_role,
       m.admin_option
FROM pg_auth_members m
JOIN pg_roles parent ON parent.oid = m.roleid
JOIN pg_roles member ON member.oid = m.member;
SELECT grantee, table_schema, table_name, privilege_type
FROM information_schema.table_privileges
WHERE table_schema NOT LIKE 'pg_%' AND table_schema <> 'information_schema';
SELECT schemaname, tablename, policyname, roles, cmd, qual, with_check
FROM pg_policies ORDER BY schemaname, tablename, policyname;
SQL
```

- [ ] Record actual table owners, row-security flags, functions, triggers, views,
  sequences, partition trees and constraints from the schema dump in step 3.
  Information-schema visibility depends on privileges; have the source owner
  confirm completeness, including large objects and extension-owned data.
- [ ] Build a source-schema/table → target-table map; resolve naming collisions
  explicitly. For partitioned tables choose one logical parent export or its
  leaf tables, never both. Record which rows are intentionally excluded.
- [ ] Separate estimates from exact counts. Partition-parent sizes do not sum all
  child storage. Budget conversion space and destination storage with indexes.

For write rate, take this sample twice at recorded wall-clock times during a
representative busy period, using fresh psql sessions each time:

```sh
PGSERVICE="$SOURCE_PGSERVICE" psql -X -v ON_ERROR_STOP=1 -c '
SELECT clock_timestamp() AS sampled_at, schemaname, relname,
       n_tup_ins, n_tup_upd, n_tup_del
FROM pg_stat_user_tables ORDER BY schemaname, relname;'
```

Compute per-table delta inserts/updates/deletes divided by elapsed seconds;
discard intervals containing counter resets. These are cumulative row-operation
counters, not a committed change log. Correlate with app transaction rate, peak
payload size, bulk jobs and provider metrics. See
[statistics](https://www.postgresql.org/docs/current/monitoring-stats.html).

## 2. Plan schema, clients, capacity and cutover

- [ ] For every object and column, record its target representation, conversion,
  query changes and verification rule using the type/feature reference. Treat
  unknown extensions as a feature gate. Do not erase unsupported functionality
  and call the migration complete.
- [ ] Map PostgreSQL roles, grants and RLS to application authorization and the
  documented destination access model. An unrestricted server API key cannot
  substitute for tenant isolation. Test denial cases as well as allowed reads.
  See [end-user data access](https://docs.terrascale.tech/guides/end-user-authentication#data-access-for-your-users).
- [ ] Choose the maintenance window unless every write path supports the bounded
  dual-write procedure in step 6. Neither path needs logical replication slots,
  publications, subscriptions or a `wal_level` change. Do not enable them for a
  dump migration: [import](https://docs.terrascale.tech/guides/import) documents
  no continuous source sync.
- [ ] Rehearse with production-sized synthetic or authorized data; measure export,
  conversion, load, index creation and verification time. Compare peak traffic
  and storage with the selected plan's [limits](https://docs.terrascale.tech/limits).
  Confirm capacity for the target, rehearsal branches and recovery copies.

### Drivers and ORMs

Follow [Postgres clients](https://docs.terrascale.tech/guides/postgres-clients)
and exercise the app's actual startup, migrations and query paths:

| Keep or change | Required action |
| --- | --- |
| psql, node-postgres, psycopg, pgx, Npgsql | Keep the driver if its queries fit. Replace provider credentials with a project API key, host with `pg.terrascale.tech`, port with `5432`, and database with project ID/database. Require TLS certificate verification. Provider IAM tokens and source roles do not authenticate here. |
| Bound queries and prepared statements | Supported, including wire `$1` parameters. Verify result types, numeric precision and binary decoding. There are 256 named prepared statements and 256 portals per session. |
| Wire spelling translation | Only the documented `SERIAL` family, `ILIKE`, `now()`, casts and numbered placeholders are translated. HTTP/CLI import SQL must already use the SQLite dialect. |
| Transactions/savepoints | Keep only transactions fitting the five-second limit and shared request limits; split large jobs. Test all rollback paths. |
| ORM SQL generation and startup | PostgreSQL catalog-dependent ORMs are unsupported. Do not promise a connection-string-only swap for Prisma, EF or another ORM. Rehearse startup and generated SQL; replace incompatible paths with the documented SQL API or an SDK. |
| Introspection, COPY, listeners, streaming cursors | Arbitrary `pg_catalog`, extensions, `LISTEN`/`NOTIFY`, `COPY FROM STDIN` and psql `\\copy` are unavailable. Selected psql describe commands work. Replace chunk-fetch portals with explicit keyset queries; protocol `Execute(maxRows)` is refused. |

Prefer the documented [SDKs](https://docs.terrascale.tech/guides/sdks) where suitable.
Do not change an ORM to a local-file driver and assume it connects to TerraScale.

## 3. Export one consistent PostgreSQL snapshot

First freeze schema changes. `pg_dump` takes a consistent snapshot by itself;
separate invocations do not automatically share one. For schema, data and source
verification to agree, keep a dedicated direct psql session open:

```sh
PGSERVICE="$SOURCE_PGSERVICE" psql -X -v ON_ERROR_STOP=1
```

In that session:

```sql
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT pg_export_snapshot();
```

Set `SOURCE_SNAPSHOT` in another shell to the returned nonsecret identifier.
Keep the exporting transaction open until exports and source verification finish.
If the session dies, abandon that export set and start a new one. A long-running
snapshot retains old row versions; agree a maximum duration with the source owner.
See [snapshot synchronization](https://www.postgresql.org/docs/current/functions-admin.html#FUNCTIONS-SNAPSHOT-SYNCHRONIZATION).

```sh
PGSERVICE="$SOURCE_PGSERVICE" pg_dump --format=plain --schema-only \
  --no-owner --no-privileges --quote-all-identifiers \
  --snapshot="$SOURCE_SNAPSHOT" --file=source-schema.sql
PGSERVICE="$SOURCE_PGSERVICE" pg_dump --format=plain --data-only \
  --inserts --column-inserts --no-owner --no-privileges \
  --quote-all-identifiers --snapshot="$SOURCE_SNAPSHOT" --file=source-data.sql
```

Check both exit statuses and all warnings. Preserve private immutable originals
and file hashes. `--no-owner` and `--no-privileges` avoid transferring ownership
and ACL statements; they do not migrate the source authorization design. Default
`pg_dump` behavior fails rather than silently dumping only RLS-visible rows;
resolve that failure with the owner, rather than enabling partial RLS exports.
Options and behavior: [pg_dump](https://www.postgresql.org/docs/current/app-pgdump.html).

- [ ] For large databases, use one data dump per inventoried logical table with
  `--table='public.customers'`, sharing the snapshot. Explicitly enumerate partition
  leaves when needed; table selection does not pull in every dependency.
- [ ] `pg_dump` has no arbitrary row-filter option. Split large table INSERTs during
  conversion; if direct row extraction is needed instead, use a PostgreSQL cursor
  under the same imported snapshot and keyset/range plan, not invented dump flags.
- [ ] Include large objects and extension data in inventory even when they are not
  ordinary table INSERTs. Materialize required bytes into the chosen target model.
  Never execute PostgreSQL large-object function calls as target SQL.
- [ ] Generated column values and materialized-view rows are not ordinary data
  INSERTs in pg_dump. If the target design materializes them, extract their values
  separately under this snapshot, or recompute them from verified inputs before
  comparing counts and digests. See the
  [pg_dump data options](https://www.postgresql.org/docs/current/app-pgdump.html).
- [ ] Before ending the keeper transaction, save exact counts and canonical row
  digests under that snapshot as described in step 5, then `COMMIT`.

For the final maintenance-window export, freeze and drain writes before creating
the snapshot. A rehearsal taken while writes continue is not the final cutover data.

## 4. Translate schema and import converted rows

### Translate before sending anything

- [ ] Write reviewed SQLite-dialect DDL into `converted-schema.sql`: explicit table
  names, keys, nullability, defaults, foreign keys, indexes and rewritten views.
  Use the local mapping reference for every PostgreSQL type and feature.
- [ ] Remove PostgreSQL `SET`, `set_config`, schema/search-path setup, extension DDL,
  sequence operations, `ALTER ... OWNER`, grants and psql meta-commands. Reimplement
  needed behavior explicitly. No raw `pg_dump` file goes to the target.
- [ ] Convert INSERTs with a SQL-aware parser or typed extraction, never regex
  replacement or splitting on semicolons/newlines. Strings may contain both,
  escaped quotes, backslashes and Unicode. Reject any unrecognized statement.
- [ ] Apply the same explicit column map to schema and values. Preserve column lists,
  explicit primary keys, NULL and empty values; convert booleans, timestamps,
  decimals, arrays, JSON and bytea as the reference specifies. Keep integers exact.
  Record converted row counts, rejected rows and all intentional transformations.
- [ ] Emit one INSERT per row with proper SQLite literal escaping, or preferably
  bound parameter objects for the HTTP route below. Do not log row payloads.
  Include a preflight proving every input statement was handled or deliberately
  replaced; unresolved or discarded data blocks cutover.

The conversion is migration-specific work; neither `pg_restore` nor
`ts db import` translates PostgreSQL definitions. Source:
[import](https://docs.terrascale.tech/guides/import).

### Apply schema, then data, within the documented limits

```sh
ts db import "$TARGET_DATABASE" converted-schema.sql
ts db import "$TARGET_DATABASE" converted-customers.sql
ts db import "$TARGET_DATABASE" converted-orders.sql
```

Load referenced rows before dependents. Foreign keys are always enforced; never
try disabling them. For cycles, design a staged nullable relationship and bounded
backfill if the model allows it, otherwise resolve the schema design before load.
Recreate backfill-sensitive triggers only at the planned point, and verify their
effects. Sources: [SQL](https://docs.terrascale.tech/concepts/sql),
[import](https://docs.terrascale.tech/guides/import).

For a typed loader, use the documented
[SQL HTTP endpoint](https://docs.terrascale.tech/guides/sql-http) and
[import batch format](https://docs.terrascale.tech/guides/import):
`POST /v1/projects/{project}/databases/{database}/sql/execute`, with an access token
obtained through the documented API-key exchange, not the raw key as a bearer token.
The JSON body has this shape, using synthetic data:

```json
{"statements":[{"sql":"INSERT INTO customers (id, email) VALUES (?, ?)","args":[1,"customer@example.com"]}]}
```

- [ ] Budget every batch for 1–256 statements, each at most 16 KiB; HTTP body at
  most 1 MiB; at most 1,024 changed rows and 8 MiB database growth per transaction.
  Include trigger/cascade effects. Honor the five-second execution limit and the
  plan's query/scan budgets. Start small and measure before increasing batch size.
- [ ] Keep an external checkpoint: source snapshot, conversion version, table,
  stable key range, file/batch hash, row count and acknowledged completion. Stream
  conversion of a large table; do not hold the entire dump or table in memory.
- [ ] Import is not atomic across batches. CLI failures name the first statement of
  the failed batch; resume the identical file with `ts db import "$TARGET_DATABASE"
  converted-customers.sql -from "$FIRST_FAILED_STATEMENT"`. Do not edit it in place.
- [ ] For ambiguous network failures, reconcile the target key range before replay.
  Explicit IDs alone do not make INSERT retries idempotent. Do not hide conflicts
  with ignore/replace semantics; compare existing values or restart a fresh target.
- [ ] A single oversize value is not fixed by reducing row count. Redesign its
  representation before cutover. Keep secondary index creation inside the same
  limits; prove that each index can be built at production size.

Limits and recovery behavior: [import](https://docs.terrascale.tech/guides/import),
[limits](https://docs.terrascale.tech/limits),
[errors](https://docs.terrascale.tech/guides/errors).

For ongoing DDL, use reviewed files with
[schema migrations](https://docs.terrascale.tech/guides/migrations) (CLI v0.0.4+).
Each file is one transaction with at most 255 user statements plus its history
row; avoid a single huge backfill migration. Choose either a migration baseline
or the schema import for initial creation, without applying the same DDL twice.

## 5. Verify against the exported snapshot

- [ ] In the keeper transaction, get exact counts for each logical source table;
  `SELECT count(*) FROM public.customers;` includes its partitions. For each
  explicit selection, record an expected converted count and compare with target
  `ts sql "$TARGET_DATABASE" 'SELECT count(*) AS n FROM customers'`.
- [ ] Full scans can exceed destination work budgets even for a one-row aggregate.
  Use indexed key ranges that fit the plan, count each disjoint range once and sum
  outside the database. Check both ends, gaps, duplicate keys and empty tables.
- [ ] Compute SHA-256 per key range outside both databases. Read source rows from
  the same snapshot, apply the conversion map, then hash stable key order and an
  unambiguous typed, length-prefixed encoding of every column. Read target rows in
  the same order and normalize to exactly that representation. Preserve NULL,
  exact integer/decimal values and byte contents. Do not compare hashes of two
  differently formatted dumps or assume a destination PostgreSQL digest function.
- [ ] For normalized one-to-many mappings, define the expected transformed row set
  and digest for every destination table. A lossy transformation needs explicit
  acceptance and a recorded equivalence test, not a misleading equal checksum.
- [ ] Page target row reads by a unique key below 10,000 rows and 8 MiB per result;
  reduce further for wide rows and scan budgets. Use indexed keyset bounds,
  including every composite-key component; avoid OFFSET drift. Sources:
  [limits](https://docs.terrascale.tech/limits),
  [indexes](https://docs.terrascale.tech/guides/indexes).
- [ ] Run `ts sql "$TARGET_DATABASE" 'PRAGMA foreign_key_check'` and inspect schema
  metadata via the allowed pragmas. Test unique/check/null invariants, new ID
  generation, JSON queries, date ranges, decimal sums and representative joins.
  See [SQL](https://docs.terrascale.tech/concepts/sql).
- [ ] Replay representative app reads and writes: boundaries, authorization denials,
  transactions, triggers and background jobs. Compare mapped results, error
  behavior and latency; create a disposable branch for destructive verification.
  Branch procedure: [branches](https://docs.terrascale.tech/guides/branches).

Record schema/data file hashes, source snapshot, table/range counts and digests,
query results, timings and unresolved differences in a private verification report.
Do not write production rows or credentials into a public migration PR.

## 6. Cut over and retain a recoverable source

### Maintenance window

1. Freeze DDL and all source writes, including queues, scheduled jobs, imports and
   direct administrative clients. Drain in-flight transactions and prove no writer
   remains. Retain the provider backup and record the freeze time.
2. Take the final snapshot/export, convert and load a fresh empty target, and run
   the complete verification. Abort within the agreed window if it fails.
3. Switch the server's secret/configuration to the rehearsed destination client
   and mapped queries. Restart pools and workers; test read/write/authorization
   paths before opening traffic. Keep the source fenced against writes.
4. Monitor errors, latency, failed jobs, counts and quotas through the observation
   period; take and test a destination backup under the documented
   [backup/restore procedure](https://docs.terrascale.tech/guides/backups-and-restore).

### Short application-managed dual-write period

Use only after rehearsal proves every writer can participate. This is application
engineering, not a TerraScale replication feature: [import](https://docs.terrascale.tech/guides/import).

1. Keep PostgreSQL authoritative. Before the baseline snapshot, deploy a durable
   outbox in the same source transaction as each insert/update/delete, with stable
   operation IDs, ordering and delete tombstones. Include jobs and admin paths.
2. Fence briefly, drain writes, record an outbox boundary and take the baseline
   snapshot. Retain the log; resume source writes with transactional capture.
3. Load the baseline, then apply only operations after that boundary through the
   same conversion map. Track ordered acknowledgements externally; implement
   atomic destination deduplication and stale-update protection. Blind concurrent
   dual writes cannot guarantee consistency across the two databases.
4. At cutover, fence source writes again, drain the outbox to a recorded boundary,
   compare final counts/digests and switch traffic. If lag cannot reach zero or a
   writer bypassed capture, abort and use the maintenance-window path.

## 7. Roll back and close the migration

- [ ] Before target writes begin, rollback is a config switch to the intact source;
  stop target clients, verify the source boundary, restart source pools and reopen
  source writes. Preserve failed target artifacts for diagnosis.
- [ ] After target writes begin, first fence and drain target writers. Reconcile
  every acknowledged target insert/update/delete back to PostgreSQL using a
  rehearsed durable journal and reverse conversion map, then verify the common
  boundary before switching. Without that journal, keep traffic paused and recover
  explicitly; switching blindly loses acknowledged writes.
- [ ] A destination snapshot or rewind does not replay writes into PostgreSQL.
  A provider snapshot does not import into TerraScale. Use the host recovery notes
  and [backups and restore](https://docs.terrascale.tech/guides/backups-and-restore)
  for each system independently. Prefer recovery into a new database for rehearsal.
- [ ] After the agreed acceptance/rollback period, revoke migration credentials,
  remove temporary connectivity, expire sensitive export artifacts and retire
  the old writer configuration. Delete source infrastructure only under the
  approved retention and decommission plan.
