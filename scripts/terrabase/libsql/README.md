# libSQL client proof scripts

One script per official libSQL client. Each runs the same flow against a TerraBase database over Hrana
(HTTP), prints every step and ends with `PASS` or `FAIL` (exit code 1 on failure):

1. `CREATE TABLE` x2 with user-chosen keys (`INTEGER PRIMARY KEY`, `TEXT PRIMARY KEY`, not ULIDs)
2. `INSERT` with parameters (positional, and named where the client supports it)
3. a batch of three inserts in write mode (one transaction)
4. `SELECT` with a `JOIN` across both tables
5. an interactive transaction that inserts, reads its own insert and commits; a second one rolls back
6. `DROP TABLE` x2 (always runs, even after a failure)

Table names are random per run (`proof_<lang>_<hex>_owners`, `..._items`), so runs never collide.
Design: [`docs/terrabase/sql/relational-sql.md`](../../../docs/terrabase/sql/relational-sql.md), section 6.

## Inputs

- `LIBSQL_URL`: `https://api.terrascale.tech/v1/projects/{project}/databases/{database}/libsql/`
  (keep the trailing slash; @libsql/client resolves `v2/pipeline` relative to it).
- `LIBSQL_TOKEN_FILE`: path to a mode-0600 file holding a current project access JWT minted through
  `client_credentials` from a dashboard API key with database read/write access. A raw API key is not a
  libSQL auth token. The scripts read the JWT from the file and never print it or take it on the command line.
  Mint immediately before each proof and use the token response's `expires_in` (currently 60 seconds);
  never retry an uncertain write merely because a token expired.

```sh
export LIBSQL_URL=https://api.terrascale.tech/v1/projects/<project>/databases/<database>/libsql/
export LIBSQL_TOKEN_FILE=$HOME/.config/terrascale/libsql-access-token   # chmod 600
```

## Run

| Client | Version | Protocol | Run from this directory |
| --- | --- | --- | --- |
| JS/TS [`@libsql/client`](https://www.npmjs.com/package/@libsql/client) | 0.18.0 | Hrana v2 pipeline | `cd js && npm ci && node proof.mjs` |
| Go [`go-libsql`](https://github.com/tursodatabase/go-libsql) (cgo) | commit `3051e37e6e04` | Hrana v3 pipeline + cursor | `cd go && CGO_ENABLED=1 go run .` |
| Rust [`libsql`](https://crates.io/crates/libsql) (`remote`, `tls`) | 0.10.0 | Hrana v3 pipeline + cursor | `cd rust && cargo run --locked` |
| Python [`libsql`](https://pypi.org/project/libsql/) | 0.1.11 | Hrana v3 pipeline + cursor | `cd python && python3 -m venv .venv && .venv/bin/pip install --require-hashes -r requirements.txt && .venv/bin/python proof.py` |

Locally, prefix builds and runs with `nice -n 19`. Go needs a C toolchain (go-libsql links a prebuilt static
library for linux/amd64, linux/arm64 and darwin/arm64). The Python wheels cover CPython 3.8 to 3.14.

## Client differences the scripts work around

- Go: `database/sql` has no batch call, and go-libsql sends only the first statement of a multi-statement `Exec`,
  so the Go batch is one transaction of three parameterized inserts.
- Rust: `execute_transactional_batch` takes SQL text without parameters, so the batch uses literals.
- Python: the module follows `sqlite3`: DML opens a transaction implicitly, `commit()` / `rollback()` end it; only
  positional parameters, and integers outside 32 bits are sent as floats.
- JS: numbers are sent as Hrana `float` values (`1` arrives as `1.0`); `INTEGER` columns rely on SQLite affinity.

## Validation status

All four pinned scripts passed against production TerraBase on 2026-10-26: parameters, joins, batches,
read-your-writes, commit and rollback. The full workflows used a forwarding relay capped at 2.78 SQL
requests per second; each official client also passed a separate direct canonical HTTPS `SELECT 1`
with normal TLS verification and no relay. These checks preceded the queued SQL edge-case fixes.

All four rejected expired access tokens and read successfully after reconnecting with a fresh token.
This proves explicit read recovery, not uninterrupted transactions across token expiry. Each script's
proof tables were removed and their absence checked through the live SQL catalog.
