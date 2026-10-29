# PostgreSQL reference control, 2026-10-29

Measured on delta through `ts-heavy-any`, using an isolated PostgreSQL 18.6 process from Nixpkgs `31ecbaca1bdefa45dffc409f0250bd8039ea0010`, Python 3.14.10, and the exact versions in `benchmark/orm-matrix/python/requirements.lock`. Three repeats per client, with separate ULID-named tables and record keys. These controls validate the probes; they do not establish TerraBase or Turso compatibility.

| Client | Connect | Introspect | Table/ALTER | FK | Index | CRUD | Commit/rollback | JSONB | Prepared | SQLSTATE/recovery | Cleanup |
|---|---|---|---|---|---|---|---|---|---|---|---|
| SQLAlchemy 2.1.6 / psycopg 3.3.6 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 |
| pgcli 4.7.1, headless PGExecute | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 |
| psql 18.6 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 |

Prepared means psycopg extended-protocol preparation for SQLAlchemy, SQL `PREPARE`/`EXECUTE` for pgcli and psql. Error assertions cover `23505`, `23503`, `42601`, `25P02`, and recovery after rollback. Raw measurements: [reference-python.jsonl.gz](reference-python.jsonl.gz), 108 rows including 18 cleanup checks.

The control exposed and corrected two psql probe issues before target measurements: a connection URL in `PGDATABASE` did not select the intended server, and tuples-only output hid schema-description footers. The final probe passes parsed libpq parameters through their environment variables and keeps schema headers/footers for `\d`.

Reproduce inside an admitted heavy job, from the repository root:

```sh
nice -n 19 nix shell github:NixOS/nixpkgs/31ecbaca1bdefa45dffc409f0250bd8039ea0010#postgresql_18 --command uv run --no-project --python 3.14.10 --with-requirements benchmark/orm-matrix/python/requirements.lock python benchmark/orm-matrix/reference.py bash -c 'result=0; for client in sqlalchemy pgcli; do python benchmark/orm-matrix/python/probe.py "$client" || result=1; done; python benchmark/orm-matrix/psql/probe.py || result=1; exit "$result"'
```
