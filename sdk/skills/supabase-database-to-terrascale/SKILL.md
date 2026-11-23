---
name: supabase-database-to-terrascale
description: Migrate Supabase database schemas and data to TerraScale with PostgreSQL exports, explicit SQLite SQL conversion, authorization replacement, reconciliation and repeatable schema migrations. Use when asked to migrate a Supabase database, move Supabase Postgres tables to TerraScale, or replace Supabase RLS and database features.
---

# Migrate a Supabase database

Use for the database portion of a migration. See also `supabase-to-terrascale`
for coordinating auth, storage, functions and client cutover. This skill is
self-contained; its references live in this folder.

## Prerequisites

- [ ] Supabase dashboard access and an app-schema allowlist.
- [ ] `supabase`, matching/newer `pg_dump`, `psql`, Python 3 and TerraScale `ts`.
- [ ] Docker running if using `supabase db dump`, which runs `pg_dump` in a container.
- [ ] A private working directory and separate destination rehearsal database.
- [ ] Source credentials supplied through libpq environment/secret files;
  `PGSERVICE`/`PGSERVICEFILE` and `PGPASSFILE` avoid passwords in command arguments.
- [ ] TerraScale CLI v0.0.4 or later for migrations, selected project, and credentials
  with the needed database and SQL permissions.

Sources: [Supabase dump](https://supabase.com/docs/reference/cli/supabase-db-dump),
[PostgreSQL dump](https://www.postgresql.org/docs/current/app-pgdump.html),
[libpq environment](https://www.postgresql.org/docs/current/libpq-envars.html),
[TerraScale CLI](https://docs.terrascale.tech/cli),
[migrations](https://docs.terrascale.tech/guides/migrations).
Never echo connection strings, run shell tracing, or commit exports. Begin with
`umask 077`; store artifacts outside the application checkout.

## 1. Inventory app schemas and dependencies

Get connection settings from Supabase **Connect**. Prefer the direct connection;
if networking prevents it, use a connection suitable for migrations as described
in [connecting to Postgres](https://supabase.com/docs/guides/database/connecting-to-postgres).
Configure `PGSERVICE` to select that source without printing its configuration.
Check `pg_dump --version` and server version before exporting; an older major
`pg_dump` refuses a newer server.

```sh
psql -X -v ON_ERROR_STOP=1 -c 'SHOW server_version'
psql -X -v ON_ERROR_STOP=1 -c "SELECT schema_name FROM information_schema.schemata ORDER BY schema_name"
psql -X -v ON_ERROR_STOP=1 -c "SELECT schemaname, tablename FROM pg_tables WHERE schemaname IN ('public') ORDER BY 1, 2"
psql -X -v ON_ERROR_STOP=1 -c "SELECT extname, extversion FROM pg_extension ORDER BY extname"
psql -X -v ON_ERROR_STOP=1 -c "SELECT schemaname, tablename, policyname, cmd, roles, qual, with_check FROM pg_policies WHERE schemaname IN ('public') ORDER BY 1, 2, 3"
```

These are source PostgreSQL queries, not destination catalog queries. PostgreSQL
catalogs are documented in [system catalogs](https://www.postgresql.org/docs/current/catalogs.html).
Record privately; policy expressions may contain application-specific literals.

- [ ] Explicitly allowlist `public` and any verified application-owned schemas.
- [ ] Skip `auth`, `storage`, `realtime`, `extensions`, `supabase_*`, `pg_*`,
  `information_schema`, and all other managed/internal schemas.
- [ ] Inspect objects inside allowed schemas too: installed extension objects or
  Supabase-managed tables are not application data just because they are public.
- [ ] Inventory FKs to `auth.users`, default functions, RPC, triggers, sequences,
  RLS, grants, indexes, partitions, generated columns and extension dependencies.
- [ ] Record schema-qualified names and choose collision-free target table names;
  for example `public.todos` becomes `todos`, `billing.todos` becomes `billing_todos`.
- [ ] Inventory every reader/writer, including direct SQL tools and cron jobs.

## 2. Plan representations and authorization

Fill a per-column map: source type/default, destination declaration, conversion,
null handling, ordering semantics, reverse conversion and verification query.
Use [type and feature mapping](references/type-and-feature-mapping.md) for every
Postgres type and feature; do not infer support from a successful connection.

TerraScale uses the SQLite dialect. Its Postgres wire translates only a few
spellings; HTTP refuses Postgres-only syntax. `COPY FROM STDIN`, `\copy`, extensions
and catalog-dependent ORMs are not supported there:
[SQL](https://docs.terrascale.tech/concepts/sql),
[Postgres clients](https://docs.terrascale.tech/guides/postgres-clients).

Decide before import:

- [ ] Preserve IDs and relationships; do not regenerate UUID primary keys.
- [ ] Resolve `auth.users` references to app-owned identity records or a validated
  identity map. Check for orphan owners before adding target foreign keys.
- [ ] Convert numeric, time, JSON, arrays and binary values without precision loss.
- [ ] Replace unsupported functions, extension queries and authorization before
  enabling any destination client.
- [ ] Use [the worked import and authorization example](references/worked-migration.md).

Do not import `CREATE POLICY`, `auth.uid()`, grants or Supabase roles. There is no
documented automatic policy translator. The public
[end-user authentication guide](https://docs.terrascale.tech/guides/end-user-authentication#data-access-for-your-users)
describes backend-minted scoped tokens with canonical ULID user IDs and rule-gated
shape/row reads. Table names asserted during minting are ignored by TerraBase;
they are not a substitute for database rules.

The [authentication capabilities page](https://docs.terrascale.tech/guides/authentication-capabilities)
takes precedence over older capability claims and says project OIDC tokens do
not authorize data APIs. Use your backend's validated session and scoped minting
flow when implementing direct reads. Follow the Rules and mint-contract links
from the end-user guide rather than inventing rule syntax. Otherwise route all
CRUD through a backend that enforces ownership, as the worked example does.

## 3. Export and translate

Freeze DDL for the export. Choose one export route; both produce PostgreSQL SQL
that must be translated, not restored directly to TerraScale.

### Supabase CLI route

Inspect installed help first. Current documentation uses `--remote` to select the
linked hosted project; default is local. In CLI v3 older selectors are deprecated
and in v4 removed. Match the linked project to the dashboard before running:

```sh
supabase db dump --help
supabase db dump --remote --schema public --file source-schema.sql
supabase db dump --remote --schema public --data-only --file source-data.sql
```

The default dump is schema-only; there is no need to invent `--schema-only` for
this command. Extend `--schema` with an audited comma-separated app-schema list.
Do not add `--use-copy`. Inspect the resulting INSERT syntax; it is still
Postgres SQL. A missing Docker daemon or unknown selector is an export failure,
not an empty successful dump. Fix the prerequisite or use `pg_dump` below.
Source: [Supabase db dump reference](https://supabase.com/docs/reference/cli/supabase-db-dump).

### PostgreSQL client route

These commands use the configured source libpq service and password file. Add
one `--schema` per audited app schema; `--schema` avoids sweeping managed schemas
into the dump. Add `--exclude-table` for any managed objects inside the allowlist.

```sh
pg_dump --schema-only --schema=public --no-owner --no-privileges --file=source-schema.sql
pg_dump --data-only --schema=public --inserts --column-inserts --no-owner --no-privileges --file=source-data.sql
```

Use `PGSERVICE` with `PGSERVICEFILE` for the intended source connection and
`PGPASSFILE` for its password. `PGDATABASE` supplies a database name; assigning a
connection URI to it does not expand that URI into host, user and password
settings. Keep password-bearing URIs out of command arguments. If only a URI is
available, provision the service and password files securely before exporting.

Sources: [pg_dump options](https://www.postgresql.org/docs/current/app-pgdump.html),
[libpq environment](https://www.postgresql.org/docs/current/libpq-envars.html),
[connection service files](https://www.postgresql.org/docs/current/libpq-pgservice.html)
and [connection parameter expansion](https://www.postgresql.org/docs/current/libpq-connect.html#LIBPQ-PQCONNECTDBPARAMS).
Each dump has its own snapshot. For final reconciliation, fence **all** source
writers before exporting and counting. Rehearsal exports taken while writes
continue cannot establish a shared final cutoff.

Translate in a private workspace, retaining both source artifacts unchanged:

1. Write `schema.sql` using the approved type map, with parents before children.
2. Remove Postgres session directives, ownership/ACL, policies, extension DDL,
   sequence operations and psql meta-commands; replace their required behavior.
3. Rewrite schema names, casts, defaults, functions and dependent SQL explicitly.
4. Decode PostgreSQL literals/types before re-encoding values for SQLite SQL.
   Do not use global search/replace on an arbitrary dump: quotes, bytea, arrays,
   multiline strings, dollar quoting and identifier collisions make it unsafe.
5. Write one row per INSERT in `data.sql`, parents before children. For complicated
   values, use a typed exporter and bound SQL/HTTP batches instead of SQL literals.
6. Add translated indexes/views/triggers after base data where appropriate;
   avoid running side-effect triggers against rows being backfilled.

The worked reference supplies an exact typed export and converter for its example.
For a different schema, extend that converter with every column's agreed mapping
and reject unrecognized fields/types. No generic converter is implied.

## 4. Import into an empty rehearsal database

Select the project explicitly. The commands below assume a signed-in CLI and
`TERRASCALE_PROJECT_ID` supplied by deployment configuration:

```sh
ts -project "$TERRASCALE_PROJECT_ID" db create app-rehearsal
ts -project "$TERRASCALE_PROJECT_ID" db import app-rehearsal schema.sql
ts -project "$TERRASCALE_PROJECT_ID" db import app-rehearsal data.sql
```

Sources: [CLI](https://docs.terrascale.tech/cli),
[import](https://docs.terrascale.tech/guides/import).
If using tracked baseline migrations, run `migrate up` instead of importing the
same schema; do not create each table twice.

`ts db import` batches a SQLite SQL script. Each batch commits separately. If a
batch fails, earlier batches remain committed; the CLI reports the 1-based first
statement number of the failed batch. Fix the cause, preserve preceding statement
numbering, and resume only after confirming the failed batch had no effects:

```sh
ts -project "$TERRASCALE_PROJECT_ID" db import app-rehearsal data.sql -from "$FAILED_BATCH_START"
```

If conversion changes earlier statements or you cannot prove the checkpoint,
start over in a new empty rehearsal database. Do not replay the whole file over
partially loaded data or hide errors with `INSERT OR IGNORE`.

For custom HTTP imports, use bound parameters, at most 256 statements, 16 KiB per
statement, 1 MiB request body and 1,024 changed rows per transaction. Account for
triggers/cascades and the 8 MiB growth limit; requests have execution deadlines.
Split oversized batches; a single oversize row needs a representation change,
not repeated requests. See [import](https://docs.terrascale.tech/guides/import)
and [limits](https://docs.terrascale.tech/limits).
Foreign keys are always enforced; do not plan to disable them. Resolve cyclic
relationships with an approved staged representation or schema redesign before
loading: [SQL](https://docs.terrascale.tech/concepts/sql).

## 5. Verify the same cutoff

Build a manifest covering **every** source app table and its target name.
Run exact `COUNT(*)` queries on both sides while the source is fenced; estimated
catalog counts are not sufficient. The example shows commands and canonical
sample checksums in [worked migration](references/worked-migration.md).

- [ ] Every table count equals its expected transformed count; document splits/merges.
- [ ] Fixed primary-key sample hashes match after the declared transformations.
- [ ] Verify null/empty values, Unicode, quotes, large IDs, numeric boundaries,
  booleans, timezone transitions, binary bytes and JSON nesting.
- [ ] Sample joins, filters, ordering, aggregates and pagination match expected results.
- [ ] Verify keys, uniqueness, orphan detection and intentional cascade behavior.
- [ ] Inspect `PRAGMA foreign_key_check`; any result blocks cutover.
- [ ] Read `sqlite_schema` and compare approved definitions, indexes and triggers.
- [ ] Exercise anonymous, owner, other-user and admin CRUD including owner changes.
- [ ] Measure representative queries using `EXPLAIN QUERY PLAN`; add approved
  indexes rather than assuming Postgres indexes transferred unchanged.

Schema introspection, allowed pragmas and foreign-key behavior are documented in
[SQL](https://docs.terrascale.tech/concepts/sql); query plans and indexes in
[indexes](https://docs.terrascale.tech/guides/indexes).
If counts differ, find missing/extra IDs before retrying. If hashes differ, compare
the privately stored canonical rows and correct the mapping. A successful import
exit status cannot waive a reconciliation or authorization failure.

## 6. Establish migrations and branches, then cut over

Keep converted schema in version control; keep production rows out. In a new
destination migration directory, create the baseline and put the reviewed schema
inside the generated file:

```sh
ts migrate new baseline
cp schema.sql migrations/0001_baseline.sql
ts -project "$TERRASCALE_PROJECT_ID" migrate up app-rehearsal
ts -project "$TERRASCALE_PROJECT_ID" migrate status app-rehearsal
```

Use that sequence on a **fresh** empty database, not one already given `schema.sql`.
Prefer it from the first rehearsal. No documented baseline-adoption command is
assumed. If schema was imported manually, rehearse again on an empty database
using migrations before final cutover.

Each migration is one transaction, with up to 255 statements of at most 16 KiB;
no `BEGIN`/`COMMIT`. Large backfills belong in bounded data-import steps. The CLI
records checksums in `ts_migrations`; never modify an applied file. See
[schema migrations](https://docs.terrascale.tech/guides/migrations).

For subsequent changes:

```sh
ts -project "$TERRASCALE_PROJECT_ID" db branch create app-change --from app --ttl 7d
ts migrate new add_todo_index
ts -project "$TERRASCALE_PROJECT_ID" migrate up app-change
ts -project "$TERRASCALE_PROJECT_ID" migrate diff app-change
ts -project "$TERRASCALE_PROJECT_ID" migrate up app
ts -project "$TERRASCALE_PROJECT_ID" migrate status app
```

Edit the generated migration before `up`. Branches are isolated snapshots and
consume database allowance; they do not merge data back. `migrate diff` compares
tables/columns/types and indexes, not defaults, NOT NULL or primary keys. Inspect
those definitions separately. See [branches](https://docs.terrascale.tech/guides/branches)
and [migrations](https://docs.terrascale.tech/guides/migrations).

Fence every writer, make the final export at one cutoff, run the rehearsed import
against the fresh final database, repeat verification, switch server credentials
and clients, then reopen writes. Keep Supabase and its backups for the rollback
window. A short dual-run requires app-owned change capture/reconciliation;
TerraScale has no live source sync: [import](https://docs.terrascale.tech/guides/import).

## 7. Roll back

Before accepting target writes, keep or restore source routing and resume source
writers only after checking source health. After accepting target writes, fence
both sides, reverse-convert and reconcile every accepted insert/update/delete,
then route back. Preserve IDs, ownership and deletion history; do not regenerate
identities or blindly reimport the original source dump. If reverse conversion
cannot preserve an accepted value, remain fenced and recover from the migration
journal under the project's declared RPO. Retire credentials and source data only
after the rollback window and explicit project acceptance.
