# W0-1: ORM and SQL dialect matrix

**Recommendation (Q-DIALECT): use SQLite dialect on Postgres wire, SQL REST, and SQL WebSocket. Describe the wire as a limited transport adapter with explicitly tested drivers; do not promise PostgreSQL ORM compatibility.** Neither tested frontend completes any client's full stock PostgreSQL workflow. This rejects a PostgreSQL dialect contract at these pins; it does not certify SQLite ORM drivers over the Postgres transport.

Measured on mariogk-dev on 2026-10-29 through `ts-heavy-any`. Each client receives a fresh frontend process and disposable database, runs three repeats with ULID-owned resources, and uses its stock PostgreSQL dialect. No SQL translation was added to the clients, and Npgsql's default type loading was preserved. The additional headless SQL client is pgcli's real `PGExecute`/catalog path, without its terminal UI. Initial mixed-client runs were replaced by isolated runs because upstream shares session state across connections.

| Component | Exact version/source |
|---|---|
| TerraBase | `35917df6c66fefd5385f6ecaee3b38f598def153`; real `src/TerraBase/Features/Postgres` and vendored native Turso |
| Turso experimental frontend | v0.8.2, `5c168be5ef6a32ac966daf5983502a4f694b37ad`, from `src/vendor/Turso.Data.Sqlite/UPSTREAM.md` |
| Prisma / adapter-pg | 7.10.1 / 7.10.1; pg 8.23.1 |
| Drizzle ORM / Kit | 0.45.4 / 0.31.11 |
| EF Core / Npgsql provider / Npgsql | 10.0.14 / 10.0.3 / 10.0.3 |
| SQLAlchemy / psycopg | 2.1.6 / 3.3.6 |
| psql / pgcli | 18.6 / 4.7.1 |
| Runtimes | Node 24.22.2, Python 3.14.10, .NET SDK `11.0.100-rc.1.26425.128` |

All six probes pass all ten checks, three repeats each, on disposable PostgreSQL 18.6. The reference is a control for the probes, not a compatibility result. Dependencies are locked. esbuild is overridden to stable 0.28.2 to remove the transitive development-server advisory; the patched graph passed the reference probes and `npm audit` reported zero vulnerabilities.

P = pass 3/3; F = workflow failed 3/3; B = prerequisite blocked all three repeats. The ten columns are distinct checks: table creation/ALTER, FK and index migrations are separate. F does not imply every possible statement in that category is unsupported.

| Frontend | Client | Connect | Introspect | Table | FK | Index | CRUD | Transactions | JSON | Prepared | Errors |
|---|---|---|---|---|---|---|---|---|---|---|---|
| TerraBase | Prisma | P | F | F | B | B | B | B | B | P | B |
| TerraBase | Drizzle | P | F | P | F | F | P | P | F | F | F |
| TerraBase | EF Core | P | F | F | F | F | F | F | F | F | F |
| TerraBase | SQLAlchemy | F | B | B | B | B | B | B | B | B | B |
| TerraBase | psql | P | B | F | B | B | B | B | B | F | B |
| TerraBase | pgcli | P | B | F | B | B | B | B | B | F | B |
| Turso postgres/ | Prisma | P | F | P | F | P | F | F | F | P | F |
| Turso postgres/ | Drizzle | P | F | P | F | P | P | P | F | P | F |
| Turso postgres/ | EF Core | F | F | F | F | F | F | F | F | F | F |
| Turso postgres/ | SQLAlchemy | F | B | B | B | B | B | B | B | B | B |
| Turso postgres/ | psql | P | F | P | P | P | P | P | P | F | F |
| Turso postgres/ | pgcli | P | F | P | P | P | P | P | P | F | F |

Failure reasons:

| Client | TerraBase current wire | Turso pinned postgres/ |
|---|---|---|
| Prisma | Migration execution returns `0A000`; native `db pull` hits an unsupported `pg_catalog` query. Dependent ORM workflows cannot start. Unnamed extended-protocol execution passes. | `ALTER TABLE ADD CONSTRAINT` is unsupported; `db pull` requires missing `current_setting`; ORM inserts fail on schema-qualified `public.<table>.id`. Bound raw execution works. |
| Drizzle | FK `ALTER TABLE ADD CONSTRAINT` and index `USING btree` return `0A000`. Native introspection fails. JSON `->>` filter returns `42601`. Named preparation/execution assertions complete, but the following cleanup `DELETE … RETURNING` returns `XX000`, failing the complete prepared workflow. The FK error check cannot succeed after its migration failed. | FK ALTER fails; native introspection fails. JSON filtering returns zero rows for the matching value. Ordinary CRUD, commit/rollback, indexes and named statement reuse pass. Constraint/syntax failures arrive with the wrong SQLSTATEs. |
| EF Core | Migrations fail on `LOCK TABLE <history> IN ACCESS EXCLUSIVE MODE` (`42601`). Reverse engineering hits unsupported catalogs; SaveChanges in a user transaction requires unsupported savepoints. Missing tables cause the remaining CRUD/JSON/prepared/error failures; those failures do not independently establish missing capabilities. | Npgsql's normal connection initialization fails on missing `pg_range` (`XX000`); every other EF check encounters that same connection prerequisite. No type-loading bypass was applied. |
| SQLAlchemy | Stock psycopg dialect initialization requires savepoints and gets `0A000`, before the first application query. | Stock initialization gets `XX000: cannot start a transaction within a transaction`, even with a fresh server for this client. |
| psql | Table/ALTER migration and SQL PREPARE return `0A000`; dependent schema/data checks are blocked. | Native `\d` fails with `SELECT requires at least one column or expression`; SQL PREPARE is unsupported. Error recovery sees `XX000`, then permits the next statement rather than PostgreSQL's `25P02`. |
| pgcli | Native connection works, but table/ALTER migration and SQL PREPARE return `0A000`. | Catalog completion fails on an aggregate ORDER BY; SQL PREPARE is unsupported. Expected UNIQUE/FK SQLSTATEs are replaced by `XX000`. |

Prepared means observed ParseComplete/BindComplete events for JS clients, explicit Npgsql preparation plus LINQ under automatic preparation for EF, psycopg `prepare=True` for SQLAlchemy, and SQL PREPARE/EXECUTE for psql/pgcli. Prisma's adapter uses unnamed statements; no named-cache claim is made. Drizzle asserts one Parse and two Binds. Basic commit/rollback passes do not qualify isolation, concurrent sessions, or crash durability.

The pinned upstream source adds reasons not to build the public contract around its translation layer: all sockets share one `PgConnection` behind a mutex; errors use `XX000`; TLS/password authentication are absent; `COMPAT.md` lists silently ignored isolation/locking clauses and no information_schema views. These are source observations, separate from the measured cells. This frontend is a lab fixture only and must not replace TerraScale's authenticated public gateway.

Choose one SQLite SQL contract for wire, REST and WebSocket, and qualify native SQL behavior in E1/E3. Keep driver/schema-discovery support explicit: changing the advertised dialect cannot make PostgreSQL catalogs, migration locks, savepoints or provider-specific JSON operators work. E3/P5 should document the supported SQL and driver versions; E12 owns production transport/authentication. This report does not measure REST, WebSocket, replication or production latency.

Reproduce inside an admitted heavy job from the repository root, with Node, the pinned .NET SDK, uv, Nix, curl, jq and flock available:

```sh
status=0
for surface in terrabase turso-postgres; do
  for client in prisma drizzle efcore sqlalchemy psql pgcli; do
    nice -n 19 bash benchmark/orm-matrix/serve.sh "$surface" bash benchmark/orm-matrix/clients.sh "$client" || status=1
  done
done
exit "$status"
```

For the PostgreSQL control, run:

```sh
nice -n 19 nix shell github:NixOS/nixpkgs/31ecbaca1bdefa45dffc409f0250bd8039ea0010#postgresql_18 --command python3 benchmark/orm-matrix/reference.py bash benchmark/orm-matrix/clients.sh
```

Each client command exits nonzero on a failed or blocked check; capture its exit status and continue the outer loop when collecting failures. The supervisor stops the server and removes its entire temporary database even when a client's SQL cleanup is unsupported. It provides disposable credentials only in the child environment. The fixtures bind ephemeral loopback ports and do not use production data or credentials. psql comes from pinned Nixpkgs, not a host installation.

The [provenance record](orm-matrix-20261029/provenance.md) identifies the 606 retained rows and probe fingerprints. Raw [reference](orm-matrix-20261029/postgres-reference.jsonl.gz), [TerraBase](orm-matrix-20261029/terrabase.jsonl.gz), [upstream](orm-matrix-20261029/turso-postgres.jsonl.gz) and [audit](orm-matrix-20261029/npm-audit.json) evidence are retained together. Run `nice -n 19 python3 benchmark/orm-matrix/summarize.py <result.jsonl.gz> ...` to regenerate all counts and detailed failure reasons; it rejects missing, duplicate or invalid primary measurements. The frontend builds use upstream's locked Rust shell and the repository's pinned native Turso download; the upstream binary measured `c216ba8a3c375e12c00c74a18fdf0fb1b81a93ce2033fb2ed30b009cac8ee097` (SHA-256).
