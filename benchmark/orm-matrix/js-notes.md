# Prisma and Drizzle PostgreSQL probes

DECISION: use Prisma 7.10.1 with matching client/adapter packages: npm's `latest` Prisma tag was 8.0.0-rc.28, while 7.10.1 was the highest stable version. Drizzle ORM 0.45.4, Kit 0.31.11, pg 8.23.1 and ulid 3.0.2 were checked against npm metadata. `js/package-lock.json` pins the entire dependency graph with integrity hashes.

Requires Node 24.11+ and a disposable PostgreSQL-wire database where the supplied user can create tables, constraints and indexes in `public`. `DATABASE_URL` must already be in the environment. `MATRIX_SURFACE` is the caller's surface label. `MATRIX_REPEAT` is a positive repeat count, default 3; each tool emits repeats 1 through that count. Run installation and probes inside the assigned `ts-heavy-any` job; commands below are relative to the repository root:

```bash
nice -n 19 npm ci --prefix benchmark/orm-matrix/js --ignore-scripts --no-audit --no-fund
MATRIX_SURFACE=postgres-reference MATRIX_REPEAT=3 nice -n 19 node benchmark/orm-matrix/js/probe.mjs
```

Prisma's schema engine must be runnable. On NixOS, Prisma does not publish a native `linux-nixos` engine for this pin. The matching x64 Debian/OpenSSL 3 binary ran on delta through its existing loader. Provision it without disabling checksum verification:

```bash
engine_dir=$(mktemp -d)
curl -fsS https://binaries.prisma.sh/all_commits/d1b3f377ca835d73fd50954a5e22128b9ebdd6b8/debian-openssl-3.0.x/schema-engine.gz -o "$engine_dir/schema-engine.gz"
gzip -d "$engine_dir/schema-engine.gz"
printf '7116e00278a793ee13db6f0a732c1aaec10f795a37bc02e3a7df3b06b2f1311f  %s/schema-engine\n' "$engine_dir" | sha256sum -c -
chmod +x "$engine_dir/schema-engine"
export PRISMA_SCHEMA_ENGINE_BINARY="$engine_dir/schema-engine"
```

Each JSONL row contains `{surface,tool,repeat,check,status,detail}`. Status is `pass`, `fail` or `blocked`; failures set exit status 1, and later stages still report. CLI output, exception messages and connection strings are not printed; diagnostics retain codes and safe command context. Every repeat owns ULID-named tables and removes them plus its generated files; `cleanup` is an additional row and its failure remains visible.

| Check | Required behavior |
|---|---|
| connect | Generate/load the actual ORM client and execute SELECT 1. |
| migrate_table / migrate_fk / migrate_index | Generate native PostgreSQL DDL through Prisma migrate diff or Drizzle Kit's snapshot/migration API, then execute each stage. FK and index stages independently require only tables. |
| introspect | Run real Prisma db pull --print or drizzle-kit introspect and require exact quoted names for both owned tables in the generated schema. Attempt even after connect fails. |
| crud | Generated ORM create/read/update/delete operations; assert returned values and absence after delete. |
| transactions | Roll back an inserted record, assert absence, then commit and read a recovery transaction. |
| json | Round-trip nested JSON, require a matching row, and reject a second row with score 8 against the score 7 filter. |
| prepared | Execute with two different bound ULIDs; observe actual pg ParseComplete/BindComplete events. Prisma uses default unnamed extended protocol, requiring at least two Parses/Binds, and makes no named-cache claim. Drizzle uses one named prepared query, requiring one Parse and two Binds on the same connection. |
| errors | Independently assert 23505, 42601 and 23503 through ORM operations; observe actual server ErrorResponse codes before ORM wrapping. Assert a subsequent query succeeds. |

Validation performed in the reserved managed job: pinned installation; Node syntax check; real Prisma client generation; all three Prisma migration diffs; all three Drizzle migration generations; both ORM factories loaded without connecting. The matching schema engine's downloaded archive checksum and uncompressed binary hash were verified. Database execution, introspection, transaction behavior and protocol counters still require the parent's disposable PostgreSQL positive control and target-surface runs; no database pass/fail result is claimed here.
