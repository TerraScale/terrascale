# PostgreSQL reference control: Prisma and Drizzle, 2026-10-29

Measured on delta through `ts-heavy-any`: three repeats against an isolated PostgreSQL 18.6 process, using the pinned Nixpkgs revision and `reference.py` from the Python control. Dependencies and Prisma's schema-engine binary are pinned in [js-notes.md](../../orm-matrix/js-notes.md). Each repeat owns ULID-named tables and record keys. These measurements validate the probes; they do not establish either target frontend's compatibility.

| Client | Connect | Introspect | Table | FK | Index | CRUD | Commit/rollback | JSONB | Parse/Bind | SQLSTATE/recovery | Cleanup |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Prisma 7.10.1, adapter-pg | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 |
| Drizzle ORM 0.45.4 / Kit 0.31.11 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 |

Prepared execution uses actual protocol counters. Prisma's adapter used unnamed statements, with at least two Parses and Binds for two values. Drizzle reused a named statement: one Parse and two Binds. These are distinct preparation behaviors, both measured successfully. Error checks cover `23505`, `23503`, `42601`, and a successful subsequent query.

The control was rerun after tightening introspection to exact quoted table names and adding a nonmatching JSON row; all checks still passed 3/3.

Raw measurements: [reference-js.jsonl.gz](reference-js.jsonl.gz), 66 rows including six cleanup checks. Installation used `npm ci --ignore-scripts` with the committed lockfile; Prisma's downloaded schema engine passed the pinned SHA-256 verification before execution.

After provisioning dependencies and `PRISMA_SCHEMA_ENGINE_BINARY` as documented, reproduce inside an admitted heavy job:

```sh
MATRIX_REPEAT=3 nice -n 19 nix shell github:NixOS/nixpkgs/31ecbaca1bdefa45dffc409f0250bd8039ea0010#postgresql_18 --command python3 benchmark/orm-matrix/reference.py node benchmark/orm-matrix/js/probe.mjs
```
