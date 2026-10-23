# Postgres driver smoke scripts

One script per Postgres client. Each connects to a TerraBase database over the Postgres wire protocol (v3), runs
the same flow, prints one `PASS` or `FAIL` line per step and ends with `PASS` or `FAIL` (exit code 1 on failure):

1. `CREATE TABLE` x2: `integer PRIMARY KEY` owners, `text PRIMARY KEY` items
2. `INSERT` with literals (simple query protocol), with parameters (extended protocol, unnamed statement) and
   through a named prepared statement executed twice
3. `UPDATE` and `DELETE` with parameters, checking the affected-row counts
4. `SELECT` with a `JOIN` and a parameter
5. `BEGIN` / `COMMIT` persists a row; `BEGIN` / `ROLLBACK` discards one
6. a duplicate primary key fails with SQLSTATE `23505`
7. a prepared `SELECT` executed twice with different parameters
8. `DROP TABLE` x2 (always runs once connected, even after a failure)

Table names are random per run (`smoke_<lang>_<hex>_owners`, `..._items`), so runs never collide. The scripts stop
at the first failed step and then drop the tables.

## Input

`TERRABASE_PG_URL`, read from the environment only (never argv). The scripts print the host and database, never
the URL or the key.

```sh
export TERRABASE_PG_URL='postgresql://smoke:<api-key>@pg.terrascale.tech:5432/<database>?sslmode=require'
```

The password is a project API key from the dashboard (`<key id>_<secret>`; TerraBase exchanges it at ts-auth and
honours its scopes and database restriction) or a TerraBase API key (`tb_…`). The user name is an ignored label, and the
database is a database name in the key's project (`<projectId>/<name>`, URL-encoded as `<projectId>%2F<name>`, for an org-wide
key). Plaintext connections are refused with SQLSTATE `28000`, so keep `sslmode=require`.

## Run

| Client | Version | Run from this directory |
| --- | --- | --- |
| Node [`pg`](https://www.npmjs.com/package/pg) | 8.23.1 | `cd js && npm ci && node smoke.mjs` |
| Python [`psycopg[binary]`](https://pypi.org/project/psycopg/) | 3.3.6 | `cd python && python3 -m venv .venv && .venv/bin/pip install --require-hashes -r requirements.txt && .venv/bin/python smoke.py` |
| Go [`pgx`](https://github.com/jackc/pgx) | v5.11.0 | `cd go && go run .` |
| .NET [`Npgsql`](https://www.nuget.org/packages/Npgsql) | 10.0.3 | `cd dotnet && dotnet run` |
| `psql` | 18+ | `./psql/smoke.sh` |

Locally, prefix installs and runs with `nice -n 19`. The Python lock targets CPython 3.14
(`uv pip compile --generate-hashes --python-version 3.14 requirements.in -o requirements.txt`).

`dotnet/` is a standalone console project, like `benchmark/dotnet/TursoSpike`: its empty `Directory.*` files keep
it out of `TerraScale.slnx`, central package management and the repository analyzers, and it commits its own
`packages.lock.json`. It pins the same Npgsql version as `tests/TerraBase.Fast`.

## Client differences

- Npgsql always uses the extended protocol, so its literal insert is an extended-protocol statement without
  parameters. Npgsql takes keyword connection strings, so the script converts the URL.
- psycopg 3 sends the simple protocol only through `ClientCursor`; `prepare=True` forces a named prepared statement.
- psql needs version 18 for `\parse` / `\bind_named` (named prepared statements; `\bind` is the unnamed one).
  `smoke.sh` splits the URL into libpq `PG*` variables. The expected unique violation also prints
  `ERROR:  23505` on stderr.

## Validation status

All five scripts pass against PostgreSQL 17.11 (`docker.io/library/postgres:17`, `sslmode=disable`, psql 18.6
client) on 2026-10-22. They have not yet run against TerraBase.
