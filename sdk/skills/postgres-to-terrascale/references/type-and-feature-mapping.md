# PostgreSQL type and feature mapping

This reference applies to PostgreSQL generally. Inventory actual data and query
semantics before choosing a representation. PostgreSQL definitions and extension
behavior are described in [data types](https://www.postgresql.org/docs/current/datatype.html),
[DDL](https://www.postgresql.org/docs/current/ddl.html) and
[extensions](https://www.postgresql.org/docs/current/extend-extensions.html).

TerraScale statements use the SQLite dialect. Values are null, 64-bit integers,
doubles, text and blobs. JSON is stored as TEXT. The Postgres wire is a driver
transport and translates only the spellings listed in its guide; it does not
establish PostgreSQL feature parity. Sources:
[SQL](https://docs.terrascale.tech/concepts/sql),
[Postgres wire, SQL dialect](https://docs.terrascale.tech/guides/postgres-clients#the-sql-dialect).

In the tables below, **no documented equivalent** means unsupported by this
migration path: implement the stated replacement and verify it. It does not
claim that every possible SQLite expression is refused. Do not ship a feature
based only on its name being accepted as a column declaration.

## Scalar and structured types

All target value representations below use the documented SQL value model above.
They are migration decisions, not automatic conversions performed by TerraScale.

| PostgreSQL source | Destination representation and required conversion |
| --- | --- |
| `smallint`, `integer`, `bigint` | INTEGER; range-check signed 64-bit. Preserve big integers without JavaScript Number rounding. |
| `serial`, `bigserial`, `smallserial` | For an auto-ID design, rewrite DDL to `id INTEGER PRIMARY KEY`; import existing IDs explicitly. Wire translates SERIAL to INTEGER only; do not assume it recreates sequence semantics. Verify the next generated ID on a disposable branch. |
| `GENERATED ... AS IDENTITY` | No documented PostgreSQL identity equivalent; use the integer primary-key design or application-issued IDs. Rewrite defaults and callers explicitly. |
| `uuid` | Canonical lowercase hyphenated TEXT, or the documented `uuid` column representation; preserve all IDs. |
| `gen_random_uuid()`, `uuid_generate_v4()` | No documented generator equivalent. Generate UUIDs in the app, bind them as text, remove source defaults. |
| `boolean` | INTEGER 0/1, preserving NULL; rewrite predicates/defaults and validate inputs on the server. |
| `real`, `double precision` | REAL/double; quantify rounding. Reject or explicitly encode NaN/Infinity rather than emitting invalid JSON/SQL values. |
| `numeric`, `decimal`, `money` | No documented arbitrary-precision numeric equivalent; scaled INTEGER when range/scale fit, otherwise canonical TEXT plus application decimal arithmetic. Never silently convert financial values to double. |
| `text`, `varchar(n)`, `char(n)` | TEXT; enforce length and padding rules in the app unless a verified target constraint supplies them. Preserve trailing-space semantics deliberately. |
| `citext` | No extension equivalent; TEXT plus an app-maintained normalized key and documented unique/expression indexes. Define Unicode normalization/case rules explicitly. |
| `bytea` | BLOB; decode the source bytes. HTTP arguments use `{"base64":"…"}`; wire uses bytea. |
| `json`, `jsonb` | JSON encoded as TEXT; rewrite queries to documented `json_extract`, `->`, `->>` or `json_each`. No binary JSONB or PostgreSQL containment/GIN parity is promised. |
| `T[]`, multidimensional arrays | No documented PostgreSQL array equivalent; normalize to child rows with ordinal columns, or JSON TEXT with explicit dimensions/null handling. Rewrite `ANY`, containment and unnest queries. |
| `CREATE TYPE ... AS ENUM` | No documented enum type equivalent; TEXT with app validation, or a lookup table and foreign key. Preserve sort order in a separate ordinal. |
| Composite types, domains | No documented equivalent; flatten to columns, child tables or JSON TEXT; enforce domain validation in the server. |
| `date` | TEXT in a chosen ISO date format; validate calendar bounds. Decide how to handle BC/infinite dates. |
| `timestamp without time zone` | TEXT with a documented local-time convention, or an explicit app conversion to UTC; do not silently assign a timezone. |
| `timestamptz` | Normalize to a fixed UTC ISO string or integer epoch unit. Preserve the instant; store the original zone separately if the app needs it. |
| `time`, `timetz`, `interval` | No documented PostgreSQL equivalents; use validated TEXT or explicit component/duration columns. Calendar months are not a fixed number of seconds. |
| `bit`, `varbit` | Validated TEXT or BLOB plus bit length; define comparison and padding in application code. |
| `inet`, `cidr`, `macaddr`, `macaddr8` | No documented network type/operators; canonical TEXT or packed BLOB, with app parsing and lookup structures. |
| Range/multirange types | No documented equivalent; boundary columns with inclusivity flags and app overlap checks. |
| `oid`, large objects | Materialize referenced bytes separately; never assume source OIDs identify destination objects. |
| Geometric/geography types | No documented PostgreSQL spatial operators; ordinary coordinate columns or serialized geometry plus an external spatial service. |
| `tsvector`, `tsquery` | No documented PostgreSQL text-search equivalent; see feature replacements below. |
| NULL, empty strings/arrays | Preserve distinctions explicitly. Never turn absent values into empty JSON, zero, or the string `null`. |

For JSON and BLOB argument forms, see [SQL](https://docs.terrascale.tech/concepts/sql).
For replacing case-insensitive searches, note that the wire maps `ILIKE` to `LIKE`
and documents ASCII case-insensitivity only:
[Postgres wire](https://docs.terrascale.tech/guides/postgres-clients).

## Features, constraints and query behavior

Supported target primitives in this section are documented in
[SQL](https://docs.terrascale.tech/concepts/sql),
[indexes](https://docs.terrascale.tech/guides/indexes) and
[Postgres clients](https://docs.terrascale.tech/guides/postgres-clients).
Everything else is an explicit application replacement or a feature gate.

| PostgreSQL feature | Equivalent or replacement |
| --- | --- |
| Schemas and search path | Explicitly rename app objects into a collision-free table map. Do not treat accepted wire `search_path` settings as a PostgreSQL namespace migration guarantee. |
| Primary keys, unique indexes | Recreate explicitly using reviewed SQLite SQL; verify conflicts, NULL behavior, generated IDs and composite keys. Plain, unique, partial and expression indexes are documented. |
| Foreign keys | Supported and always enforced, including ON DELETE CASCADE. Load parents first; resolve cross-table references and cycles. `PRAGMA foreign_key_check` is allowed; do not disable enforcement. |
| CHECK constraints | PostgreSQL expressions are not portable. No automatic conversion is documented. Rewrite supported expressions and prove rejection of bad values on the destination; otherwise enforce each invariant on the server. |
| Generated columns | No PostgreSQL generated-column compatibility contract is documented. Materialize into ordinary columns and maintain values on all write paths; use target generated syntax only after proving the exact expression and behavior in rehearsal. |
| Views | Supported; rewrite the view query to SQLite SQL, qualify mapped table names and compare result samples. |
| Materialized views / REFRESH | No documented equivalent; use ordinary summary tables refreshed by an app job or documented function schedule. Define staleness and failure handling. |
| Triggers | Supported; rewrite bodies to SQLite SQL and verify effects. PostgreSQL trigger functions are not transferred. Enable backfill-sensitive behavior only at the planned stage. |
| PL/pgSQL functions, stored procedures, RPC | No documented PL/pgSQL runtime or PostgreSQL stored-function compatibility; move logic to app code or documented JavaScript functions and SQL transactions. |
| Sequences, nextval/currval/setval | No documented equivalent; use integer primary keys or app-issued IDs. Audit sequence-dependent ordering and externally shared counters. |
| Row-level security, grants, roles | No automatic PostgreSQL RLS translation; use scoped tokens plus database rules for documented read paths, or server-enforced checks. Inventory every policy and grant; test authorized and denied access after replacement. |
| Transactions, savepoints | Wire supports transactions/savepoints within five seconds. HTTP batches are atomic per request and refuse explicit BEGIN/COMMIT/ROLLBACK. Split backfills; redesign long transactions. |
| GIN/GiST/BRIN, operator classes, INCLUDE | No documented equivalents; replace with the documented plain/unique/partial/expression indexes and redesigned queries. Confirm with EXPLAIN QUERY PLAN. |
| Full text search | No documented PostgreSQL text-search equivalent. Virtual tables are refused, so do not propose FTS virtual tables. Use an external search index or bounded app search. |
| Partitioning, inheritance | No documented PostgreSQL equivalents; flatten into ordinary tables or app-managed partitions with explicit routing and constraints. |
| LISTEN/NOTIFY | Refused over wire. Use documented live shapes where their contract fits, or an application messaging service for event delivery. |
| FULL OUTER JOIN | Refused; docs prescribe LEFT JOIN with UNION ALL. Verify duplicate and NULL semantics. |
| Temporary/virtual tables, ATTACH/DETACH | Refused. Use app-side intermediate data or ordinary staging tables under an explicit lifecycle. |
| COPY FROM STDIN, psql \copy | Not supported on the wire. Convert rows to SQL statements or bound HTTP batches per the import guide. |
| Postgres casts, now(), ILIKE | Limited wire translation exists; converted import SQL must use SQLite syntax because CLI import uses the documented SQLite SQL path. |
| Catalog-dependent tooling | Arbitrary pg_catalog queries and catalog-dependent ORMs are not supported. Use the documented SQL API/SDK and target schema introspection. |

## Extensions

`CREATE/ALTER/DROP EXTENSION` is refused over the Postgres wire; source extension
binaries and their objects do not migrate. Source:
[Postgres clients](https://docs.terrascale.tech/guides/postgres-clients).

| Extension | Unsupported source behavior and replacement |
| --- | --- |
| `pgvector` | No documented vector type, ANN operator or vector index equivalent. Keep canonical embeddings in app data if useful; use an external vector search service and reconcile by stable IDs. |
| `postgis` | No documented spatial type/index/query equivalent. Export a chosen portable geometry format and use an external spatial service or app calculations. |
| `pg_cron` | Extension jobs do not transfer. Use app scheduling or TerraScale JavaScript functions with documented UTC cron scheduling. |
| `uuid-ossp` | UUID generation functions do not transfer. Generate IDs in application code; preserve imported IDs. |
| `pgcrypto` | No documented extension equivalent; move cryptographic operations to a maintained application library and keep key material in a secret manager. |
| `hstore`, `ltree` | No documented operators/types; JSON TEXT or normalized hierarchy tables, with rewritten queries. |
| Other extensions | Treat as unsupported until the public destination docs document the needed behavior; isolate or replace it before cutover. |

Function runtime and cron are documented in
[functions](https://docs.terrascale.tech/guides/functions).
For authorization, consult
[end-user authentication](https://docs.terrascale.tech/guides/end-user-authentication#data-access-for-your-users)
and [authentication capabilities](https://docs.terrascale.tech/guides/authentication-capabilities).
For import conversion and non-atomic batch recovery, consult
[import](https://docs.terrascale.tech/guides/import).
