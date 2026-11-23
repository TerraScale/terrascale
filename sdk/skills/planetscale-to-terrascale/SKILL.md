---
name: planetscale-to-terrascale
description: Migrate PlanetScale Vitess, MySQL and MariaDB databases to TerraScale with consistent exports, explicit schema and SQL translation, batched imports, driver changes and verified cutover. Use when asked to migrate from PlanetScale, move off MySQL or MariaDB, replace mysql2 or the PlanetScale serverless driver, or migrate PlanetScale branches and deploy requests.
---

# Migrate PlanetScale, MySQL or MariaDB

Use this workflow for relational application data. First distinguish PlanetScale
Vitess/MySQL from PlanetScale Postgres. For Postgres, take the generic PostgreSQL
path in prose: inventory PostgreSQL objects, export with `pg_dump`, translate the
schema and values to the SQLite dialect, then follow the TerraScale import guide.
Do not run this skill's MySQL conversions or `pscale database dump` on Postgres.
PlanetScale documents its separate [Postgres dump/restore workflow][ps-postgres];
TerraScale requires [translated inserts and does not accept COPY][import].

## Prerequisites

- [ ] Source organization, database and production branch identified; source
  credentials and permission to read schema, rows and relevant metadata.
- [ ] `pscale` for PlanetScale; a compatible `mysql`/`mysqldump` for MySQL, or
  `mariadb`/`mariadb-dump` for MariaDB; source CA certificates for verified TLS.
- [ ] TerraScale project, empty rehearsal database, and CLI installed; `ts migrate`
  requires CLI v0.0.4 or later: [CLI][cli], [migrations][migrations].
- [ ] Destination key scoped to the intended database with `sql:read` and
  `sql:write`; provisioning branches also needs database permissions:
  [authentication][auth], [branches][branches].
- [ ] Private export directory outside Git, enough local disk, measured maintenance
  window, and a tested application switch back to the source.

Start the export shell with `umask 077`. Supply credentials from the deployment's
secret store or private client option files. Never print credentials, URLs with
passwords, API responses containing keys, or row contents into shared logs.
Do not enable shell tracing. Example names such as `customers` must be replaced
with the audited application allowlist.

Read [type and syntax mapping](references/mysql-type-and-syntax-mapping.md) when
planning conversions, then [code examples](references/code-examples.md) when
changing application clients. Both references are self-contained with this skill.

## 1. Inventory the source and all writers

Check installed help before using a CLI version's flags:

```sh
pscale --version
pscale org list
pscale org show
pscale database list --org "$PLANETSCALE_ORG" --format json
pscale database show "$SOURCE_DATABASE" --org "$PLANETSCALE_ORG" --format json
pscale branch list "$SOURCE_DATABASE" --org "$PLANETSCALE_ORG" --format json
pscale branch show "$SOURCE_DATABASE" "$SOURCE_BRANCH" --org "$PLANETSCALE_ORG" --format json
pscale deploy-request list "$SOURCE_DATABASE" --org "$PLANETSCALE_ORG" --format json
```

Sources: PlanetScale [CLI overview][ps-cli], [org][ps-org],
[database][ps-db], [branch][ps-branch], [deploy requests][ps-deploy].
If the website is unavailable, confirm command definitions in the public
[CLI source](https://github.com/planetscale/cli/tree/main/internal/cmd).
Paginate database and branch lists when necessary; record each branch's purpose,
production status, pending deploy requests and intended schema. Do not mistake a
development branch's data for the production dataset.

- [ ] Record database kind from CLI metadata and confirm in the dashboard.
  Vitess/MySQL continues here; Postgres takes the path above.
- [ ] Record source version, storage engines, SQL mode, server/session time zones,
  charset, column collations, sharding/keyspaces and region/replica topology.
- [ ] Capture branch sizes from the available CLI metadata/dashboard; never invent
  a size subcommand. Confirm table sizes using source SQL below. Estimates are
  capacity inputs, not exact row counts.
- [ ] Inventory tables, primary/unique keys, secondary indexes, foreign keys,
  views, triggers, routines, events, generated columns and application sequences.
- [ ] For Vitess, identify keyspace/shard coverage, absent foreign keys,
  cross-shard relationships, and any routing/vindex assumptions in application SQL.
- [ ] Find every writer: web, queues, scheduled jobs, admin scripts, imports,
  third-party integrations and old application versions.
- [ ] Inventory MySQL-specific queries and driver behavior: decimal/bigint
  coercion, timezone conversion, insert IDs, affected rows and transaction APIs.

Run these against the **source**, using a private MySQL option file selected by
`MYSQL_OPTION_FILE`; `--defaults-extra-file` must be the first option:

```sh
mysql --defaults-extra-file="$MYSQL_OPTION_FILE" --database="$SOURCE_DATABASE" --batch <<'SQL'
SELECT VERSION(), @@sql_mode, @@session.time_zone, @@system_time_zone;
SELECT TABLE_NAME, ENGINE, TABLE_ROWS, DATA_LENGTH, INDEX_LENGTH, AUTO_INCREMENT
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME;
SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT,
       EXTRA, CHARACTER_SET_NAME, COLLATION_NAME
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME, ORDINAL_POSITION;
SELECT TABLE_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME
FROM information_schema.KEY_COLUMN_USAGE
WHERE TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL;
SQL
```

Sources: MySQL [option files][mysql-options], [TABLES][mysql-tables],
[COLUMNS][mysql-columns], [KEY_COLUMN_USAGE][mysql-keys]. Configure the private
option file with host/user/password, `ssl-mode=VERIFY_IDENTITY` and the source CA;
do not disable TLS verification. MariaDB uses its own client's verified TLS
options: [MariaDB client](https://mariadb.com/docs/server/clients-and-utilities/mariadb-client).
On Vitess, unavailable metadata is a gap to resolve from dashboard/schema exports,
not evidence that a feature or table is absent. Inspect each `SHOW CREATE TABLE`
and exact counts separately; `TABLE_ROWS` can be approximate.

## 2. Plan schema, semantics and downtime

TerraScale uses the SQLite dialect on every SQL surface. Its Postgres wire is a
driver transport with a few translated spellings, not PostgreSQL compatibility.
Do not point a MySQL driver at it: [SQL][sql], [Postgres clients][pg].

Write a per-column conversion ledger: source declaration and range, destination
declaration, value transformation, null/default handling, application encoding,
reverse conversion, and verification query. Resolve every row in the
[mapping reference](references/mysql-type-and-syntax-mapping.md) before importing.

- [ ] Preserve primary keys and dependent IDs exactly. Record AUTO_INCREMENT
  high-water marks, including deleted IDs; do not regenerate IDs while importing.
- [ ] Choose exact storage for DECIMAL and unsigned 64-bit values; prohibit
  JavaScript `Number` coercion when values exceed its exact integer range.
- [ ] Decide whether each date/time is an instant or a local wall-clock value.
  Resolve zero dates and daylight-saving ambiguities before conversion.
- [ ] Define and version application normalization for case/accent-insensitive
  comparisons. Detect collisions before creating destination unique indexes.
- [ ] Reimplement MySQL routines/events and SQL-mode-dependent behavior in the
  application or reviewed SQLite SQL. No automatic MySQL translator is documented
  in TerraScale's [import guide][import].
- [ ] Estimate destination storage, database slots, operation budget and import
  duration from a rehearsal. Branches consume database allowance; request and
  plan limits apply to imports: [branches][branches], [limits][limits].
- [ ] Choose a final write-fenced export. Do not promise binlog replication,
  Vitess replication or automatic dual-write reconciliation: the import guide
  documents batched SQL loading, not a MySQL live migration service: [import][import].

Keep an explicit Product gaps list in the migration plan: feature, affected query,
replacement, verification and rollback effect. An unresolved requirement blocks
cutover even if all rows imported successfully.

## 3. Export a consistent dataset

Freeze source DDL before export. A rehearsal can run with writes active only if
the chosen exporter guarantees the needed snapshot. For the final export, fence
all writers, drain in-flight transactions and record a cutoff marker before
exporting/counting. Keep the fence until cutover or cancellation.

### PlanetScale Vitess route

```sh
pscale database dump --help
pscale database dump "$SOURCE_DATABASE" "$SOURCE_BRANCH" --org "$PLANETSCALE_ORG" --output "$EXPORT_DIR"
```

`--output` selects a **directory**, not a single SQL filename. The default data
format is MySQL SQL. Record its table/chunk manifest and exit status, and retain
schema files alongside the data: [database dump][ps-dump],
[command source](https://github.com/planetscale/cli/blob/main/internal/cmd/database/dump.go).
Do not invent a `--single-transaction` option for `pscale database dump`. Its
parallel table reads are not proof of one cross-table/cross-shard snapshot;
the final write fence is required for this route.

For sharded/multiple-keyspace databases, inspect the installed command's
`--keyspace` and `--shard` options and dump every audited logical partition.
Never assume one default dump covers every keyspace. Verify whether returned
rows are already aggregated before combining shards; avoid duplicating rows.
Avoid replica/read-only-region dumps for the final cutoff unless replication
catch-up is independently established. Retain a per-partition completeness record.

### MySQL or MariaDB connection route

Use a compatible client against a verified TLS connection. For MySQL, with an
audited table allowlist in place of these two example tables:

```sh
mysqldump --defaults-extra-file="$MYSQL_OPTION_FILE" \
  --single-transaction --quick --skip-lock-tables \
  --set-gtid-purged=OFF --no-tablespaces --column-statistics=0 \
  --default-character-set=utf8mb4 --tz-utc --hex-blob \
  --complete-insert --skip-extended-insert \
  --skip-add-locks --skip-disable-keys --skip-triggers \
  "$SOURCE_DATABASE" customers orders > "$EXPORT_DIR/source.sql"
```

`--single-transaction` gives a consistent snapshot for transactional tables such
as InnoDB; it does not protect nontransactional tables or concurrent DDL. Keep
DDL frozen and fence writes for mixed engines. `--quick` streams rows rather than
buffering whole tables. One row per INSERT and column names simplify conversion;
they do not make the dump valid destination SQL: [mysqldump][mysql-dump].
Capture routines/events/triggers separately for reimplementation; the data export
above intentionally omits triggers. Check all process exit codes and disk space;
a nonempty file is not proof of a successful dump.

MariaDB: use `mariadb-dump` and its matching private client configuration with
`--single-transaction --quick --skip-lock-tables --hex-blob --complete-insert
--skip-extended-insert --skip-add-locks --skip-disable-keys --skip-triggers`.
Do not copy MySQL's GTID, column-statistics or TLS flags blindly to MariaDB.
Preserve its timestamp conversion settings explicitly and inspect any sandbox
preamble as source-only metadata: [mariadb-dump][maria-dump].
For a parallel `mydumper` workflow, use the installed version's documented
transactional snapshot/locking mode, capture its metadata and coordinate all
workers. Do not substitute parallel independent `mysqldump` processes and call
that one snapshot. The single-client route above is sufficient here.

## 4. Translate, create tables and import batches

Work from immutable source artifacts. Convert DDL and values structurally with a
MySQL-aware parser or a typed source reader; global search/replace corrupts quoted
literals, escapes, backticks and JSON. Retain a conversion manifest and rejected
rows report privately. Rejected values require an explicit resolution.

1. Create reviewed SQLite-dialect tables and required unique keys using versioned
   migrations. Strip engine/charset clauses, MySQL SET directives, version comments,
   lock statements, GTID state and dump transaction wrappers. Translate data
   literals, including MySQL backslash escapes and binary values, separately.
2. Import explicit IDs, parent tables before children, and one row per statement
   with an explicit column list. Prefer bound parameters for large values.
3. Preserve keys needed for integrity during loading; add other secondary indexes
   after data load and inspect their plans: [indexes][indexes].
4. Run verification before enabling application writers.

TerraScale has no file-upload restore endpoint. For converted SQL scripts:

```sh
ts migrate up "$TARGET_DATABASE"
ts db import "$TARGET_DATABASE" "$EXPORT_DIR/converted-data.sql"
```

The CLI splits the import into batches; the entire import is **not atomic**.
On failure, it prints the 1-based first statement number of the failed batch.
Resolve the cause without altering preceding statement order, then resume with
`ts db import "$TARGET_DATABASE" "$EXPORT_DIR/converted-data.sql" -from "$RESUME_STATEMENT"`.
If commit outcome is uncertain, reconcile the batch before replay. Rehearsals are
easiest to restart on a new empty database: [import][import],
[errors](https://docs.terrascale.tech/guides/errors).

For SDK/HTTP imports, each request contains 1–256 statements and is one atomic
transaction. Keep SQL text at most 16 KiB per statement, serialized request body
at most 1 MiB, rows changed at most 1,024 and file growth at most 8 MiB per
transaction. Execution has a 5-second request limit, with lower query timeouts
on some plans. Count trigger/cascade effects too: [import][import], [limits][limits].
Split on encoded byte size as well as row count. A row that cannot fit needs a
representation change or separately designed storage path, not endless retries.
HTTP blobs use `{"base64":"…"}`; integers need exact encoding. Do not wrap HTTP
batches in `BEGIN`/`COMMIT`: [SQL HTTP][http], [SQL][sql].

### Foreign keys missing in a Vitess schema

TerraScale always enforces foreign keys; disabling them is not a migration
strategy: [SQL][sql]. Declare validated constraints up front where possible.
After a staging import, run orphan queries such as:

```sql
SELECT o.id FROM orders o
LEFT JOIN customers c ON c.id = o.customer_id
WHERE o.customer_id IS NOT NULL AND c.id IS NULL LIMIT 100;
```

To add a foreign key **after** importing, rebuild the affected table with the
constraint, rather than issuing MySQL's `ALTER TABLE ... ADD CONSTRAINT`:

- Create `orders_checked` with reviewed final columns and
  `FOREIGN KEY (customer_id) REFERENCES customers(id)`.
- Copy explicit columns from `orders` in disjoint primary-key ranges, one bounded
  transaction at a time. Check counts, values and orphan results after the copy.
- For a leaf table with no inbound references, dependent views or triggers, swap
  using `DROP TABLE orders; ALTER TABLE orders_checked RENAME TO orders;` in one
  HTTP batch, then recreate its indexes. Keep application writers fenced.
- For an inbound dependency graph, plan the complete rebuild order first; do not
  use the leaf-table swap on a referenced parent. Prefer a fresh final database
  with constraints declared and a parent-first reimport if a safe bounded rebuild
  cannot be established. Do not exceed transaction limits to force one large copy.
- Run `PRAGMA foreign_key_check` and inspect `PRAGMA foreign_key_list(orders)`.

The rebuild is a SQLite-dialect migration design; verify it on the rehearsal
database. Allowed inspection pragmas, enforcement and transaction limits are
documented in [SQL][sql]; record the final DDL in [migrations][migrations].

## 5. Replace drivers, ORMs and branch workflows

Use [the before/after examples](references/code-examples.md) for
`@planetscale/database` and `mysql2`. Map result shapes, insert IDs and error
handling deliberately. A host/password change does not translate MySQL SQL.
Keep privileged keys on the server: [authentication][auth].

- [ ] For an HTTP-only/serverless runtime, use [SQL over HTTP][http] with an
  exchanged access token, or the SDK described by the [JS quickstart][js].
- [ ] The JS quickstart currently describes source installation; do not assume
  the previous published SDK contains its current exports: [JS quickstart][js].
- [ ] Prisma: switch the schema provider from MySQL to SQLite, remove MySQL
  native annotations, regenerate the client and use the documented adapter.
  The adapter is marked next release; use source only when that dependency is
  acceptable. `prisma migrate dev` and `prisma db push` are unsupported; generate
  reviewed SQL and apply with `ts migrate`: [Prisma][prisma].
- [ ] Drizzle: change `mysql-core`/MySQL drivers to `sqlite-core` and
  `sqlite-proxy` with the documented adapter. It is also marked next release.
  Use `drizzle-kit generate`, not push/introspection. Use `orm.batch` for atomic
  writes; the proxy's `orm.transaction` is not atomic: [Drizzle][drizzle].
- [ ] Port every raw query as well as ORM schema declarations. Replay business
  transactions and verify decimal, bigint, datetime and blob return types.

PlanetScale branches/deploy requests become an explicit migration workflow;
they are not imported as deploy-request objects:

| PlanetScale workflow | TerraScale migration workflow |
| --- | --- |
| Development branch | Independent snapshot database; later writes are isolated: [branches][branches]. |
| Branch schema changes | Numbered immutable `migrations/NNNN_name.sql` files: [migrations][migrations]. |
| Deploy request review | Review generated SQL, apply to a branch, verify, then apply those files to the destination: [migrations][migrations]. |
| Per-PR database | Branch action creates/reuses a preview and cleans up on PR close: [branches][branches]. |

```sh
ts db branch create migration-rehearsal --from "$TARGET_DATABASE" --ttl 7d
ts migrate new add_orders_index
ts migrate up migration-rehearsal
ts migrate diff migration-rehearsal
ts migrate status migration-rehearsal
```

Do not treat `migrate diff` as complete constraint verification: it does not compare
NOT NULL, defaults or primary keys. Each migration file is atomic, holds at most
255 statements and includes a history write; keep it inside all write limits:
[migrations][migrations], [limits][limits]. Branch changes are not merged back as
data; apply the same reviewed migration files to the destination.

For per-PR databases, follow [the branch-action README][action] and
[branches][branches]. The README marks the guarded source contract **unreleased,
under publication hold**; released `v1` does not supply that contract. Do not
present it as production-ready automation. When the required CLI/server routes
are released and verified, pin the reviewed full action SHA, serialize runs per
PR, use `pull_request` events, synthetic seeds and the masked `connection-url`
output. Fork PRs do not receive repository secrets. Until readiness is verified,
use manually managed rehearsal branches from the documented CLI, subject to its
guarded-route availability; never fall back to unguarded cleanup.

## 6. Verify against the final cutoff

- [ ] Match the audited table/partition manifest and exact per-table counts.
  Compare primary-key ranges, null counts and canonical per-key-range digests;
  a source engine checksum cannot be compared across representations.
- [ ] Compare boundary samples: large/signed/unsigned IDs, exact decimals, blobs,
  zero/nullable dates, fractional seconds, DST edges, emoji, accents and case.
- [ ] Verify unique-key normalization collisions, all orphan queries and
  `PRAGMA foreign_key_check`; inspect defaults, checks, indexes and triggers.
- [ ] Verify new ID allocation cannot reuse a source high-water mark accidentally.
- [ ] Replay login-linked lookups, pagination, search, upserts, deletes/cascades,
  background jobs and multi-statement business transactions on synthetic data.
- [ ] Inspect `EXPLAIN QUERY PLAN` for hot queries and measure latency with the
  intended indexes. Page reconciliation reads below result/scan limits:
  [indexes][indexes], [limits][limits].
- [ ] Confirm application authorization, key scopes, token refresh, permanent
  authorization errors and ambiguous write outcomes: [auth][auth],
  [errors](https://docs.terrascale.tech/guides/errors).

Store a private reconciliation report with cutoff time, artifact hashes, counts,
conversion version, mismatches and resolution. Zero unresolved mismatches and
verified application behavior are the cutover gate.

## 7. Cut over and retain a rollback boundary

1. Complete a timed rehearsal including source fencing and rollback.
2. Fence production writers, drain queues/transactions, export again and import
   into the prepared final database; never layer a new full dump over stale rows.
3. Repeat final verification against that frozen source. Record migration status
   and the artifact manifest; switch all readers and writers together.
4. Deploy the new driver/configuration, refresh connections and run a controlled
   read/write canary before reopening traffic. Observe errors, queues, constraints
   and latency. Keep the source intact and write-fenced during the rollback window.
5. Before accepting new writes, define how destination-only changes will be
   captured and reverse-converted; without that mechanism, automatic rollback
   ends at the first destination write.

## 8. Roll back without losing destination writes

Before destination writes, fence the destination, switch configuration back to
the unchanged source, verify connectivity, then reopen source writers.

After destination writes, first fence all writers and capture the destination
state using [SQL export][http]. Extract destination-only inserts, updates and
deletes relative to the cutoff manifest or the application's durable change
journal. Reverse the conversion ledger into source types, resolve conflicting
keys and timestamp/normalization semantics, then reconcile exact rows/counts
before reopening the source. A snapshot alone does not identify deleted rows
without a baseline. No automatic reverse replication is documented: [import][import].

If reconciliation cannot be completed, keep the write fence and recover forward;
do not silently discard post-cutover writes. Retire source access and private
exports only after the agreed rollback and retention windows.

[sql]: https://docs.terrascale.tech/concepts/sql
[pg]: https://docs.terrascale.tech/guides/postgres-clients
[import]: https://docs.terrascale.tech/guides/import
[http]: https://docs.terrascale.tech/guides/sql-http
[auth]: https://docs.terrascale.tech/guides/authentication
[js]: https://docs.terrascale.tech/getting-started/javascript
[cli]: https://docs.terrascale.tech/cli
[limits]: https://docs.terrascale.tech/limits
[migrations]: https://docs.terrascale.tech/guides/migrations
[branches]: https://docs.terrascale.tech/guides/branches
[indexes]: https://docs.terrascale.tech/guides/indexes
[prisma]: https://docs.terrascale.tech/guides/orm-prisma
[drizzle]: https://docs.terrascale.tech/guides/orm-drizzle
[action]: https://github.com/TerraScale/branch-action
[ps-cli]: https://planetscale.com/docs/cli/pscale
[ps-org]: https://planetscale.com/docs/cli/org
[ps-db]: https://planetscale.com/docs/cli/database
[ps-branch]: https://planetscale.com/docs/cli/branch
[ps-deploy]: https://planetscale.com/docs/cli/deploy-request
[ps-dump]: https://planetscale.com/docs/cli/database-dump
[ps-postgres]: https://planetscale.com/docs/postgres/imports/postgres-migrate-dumprestore
[mysql-options]: https://dev.mysql.com/doc/refman/8.4/en/option-files.html
[mysql-tables]: https://dev.mysql.com/doc/refman/8.4/en/information-schema-tables-table.html
[mysql-columns]: https://dev.mysql.com/doc/refman/8.4/en/information-schema-columns-table.html
[mysql-keys]: https://dev.mysql.com/doc/refman/8.4/en/information-schema-key-column-usage-table.html
[mysql-dump]: https://dev.mysql.com/doc/refman/8.4/en/mysqldump.html
[maria-dump]: https://mariadb.com/docs/server/clients-and-utilities/backup-restore-and-import-clients/mariadb-dump
