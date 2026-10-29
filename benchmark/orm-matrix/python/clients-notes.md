# Stock PostgreSQL client probes

Run from the repository root in the parent's admitted heavy job, with `DATABASE_URL` supplied privately as a PostgreSQL URL, `MATRIX_SURFACE` set to the endpoint label, and `MATRIX_REPEAT=3` (default). Repeats run numbered 1..N; every repeat creates separate ULID-named tables and ULID record keys. Use a disposable database with DDL permission. Never put the URL on the command line or enable shell tracing.

```sh
nice -n 19 uv run --no-project --python 3.14.10 --with-requirements benchmark/orm-matrix/python/requirements.lock python benchmark/orm-matrix/python/probe.py sqlalchemy
nice -n 19 uv run --no-project --python 3.14.10 --with-requirements benchmark/orm-matrix/python/requirements.lock python benchmark/orm-matrix/python/probe.py pgcli
nice -n 19 bash benchmark/orm-matrix/psql/run.sh
```

Each command emits JSONL `{surface,tool,repeat,check,status,detail}` to stdout. The ten matrix checks are `connect`, `introspect`, `migrate_table`, `migrate_fk`, `migrate_index`, `crud`, `transactions`, `json`, `prepared`, `errors`; dependency order puts introspection after DDL. `cleanup` and `cleanup_connection` are additional resource checks. Any failure or blocked check exits nonzero. A failed initialization blocks dependent checks with its original redacted cause; there is no diagnostic fallback counted as a client pass. Dependency downloads may write progress to stderr. Capture stdout to the parent's raw results and publish tables under `benchmark/bench/`.

SQLAlchemy uses the unmodified PostgreSQL psycopg dialect, imperative ORM mapping, metadata `create_all` and `reflect`, native FK/index inspection, ORM CRUD with commit and rollback, PostgreSQL JSONB and psycopg `prepare=True` with two parameter values. Its explicit SQL transaction check additionally proves a committed insert and rolled-back update. pgcli uses the real `PGExecute` constructor and `run`, retaining all its initialization and typecaster behavior; `tables`, `table_columns`, `schemata`, and `foreignkeys` are its native completion catalog APIs. This is its headless SQL-client path, not its terminal UI or a BI product. psql is the genuine Nix PostgreSQL 18.6 binary and executes `\dt` and `\d` for native discovery. Each psql check launches a fresh process; transaction, preparation, and each error/recovery sequence keep their statements together in one process.

The pgcli and psql prepared checks use PostgreSQL SQL `PREPARE`/`EXECUTE`; these do **not** prove extended-wire Parse/Bind parameter handling. The SQLAlchemy prepared check does exercise psycopg's extended protocol. Every client asserts UNIQUE `23505`, FK `23503`, syntax `42601`, failed-transaction `25P02`, and successful recovery after rollback. PostgreSQL SQL is not rewritten to SQLite. DDL success means the client completed the statements; catalog, constraint and data checks independently verify behavior.

DECISION: pgcli satisfies the requested additional headless SQL client because its real native initialization/catalog APIs are directly callable without a terminal. These probes make no BI product compatibility claim. DECISION: use pinned PostgreSQL 18.6; the same Nix revision's PostgreSQL 19 package is `19rc1`, not stable.

Version metadata checked against PyPI and pinned Nixpkgs source on 2026-10-29: SQLAlchemy 2.1.6, psycopg/psycopg-binary 3.3.6, pgcli 4.7.1, python-ulid 4.1.0. Python 3.14.10 was resolved by uv. `requirements.lock` pins all 17 resolved dependencies; resolution succeeded for Python 3.14. Runtime package imports and endpoint integration runs remain for the parent after source integration; no endpoint outcomes are claimed here.
