---
title: Python quickstart
description: Create tables, insert rows and query typed rows with the Python SDK.
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Python quickstart

Run a complete SQL workflow against your TerraScale database in about five minutes: create a table, insert rows with parameters, read typed rows back, run a multi-statement batch and handle a refused statement.

## Prerequisites

- Python 3.10 or newer
- A TerraScale account, a project with a database, and an API key. Follow steps 1–4 of [Getting started](/pt-br/getting-started/): the key needs the **sql:read** and **sql:write** scopes on that database.
- The **Current project** ID (a ULID) from the dashboard's **Organization** page, and the API key. Export both:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
export TERRASCALE_DATABASE_NAME=main                       # your database's name
```

## Install the SDK

The package is not on PyPI yet; install it from the monorepo:

```bash
python -m pip install "git+https://github.com/TerraScale/terrascale@main#subdirectory=sdk/python"
```

## Run the quickstart

Save this as `quickstart.py`. The client reads the key and project from the environment and exchanges the key for short-lived access tokens automatically.

```python
import os
from dataclasses import dataclass

from terrascale import SqlError, TerraScale, new_id

with TerraScale() as ts:                    # reads TERRASCALE_API_KEY / TERRASCALE_PROJECT_ID
    db = ts.database(os.environ["TERRASCALE_DATABASE_NAME"])

    # One statement, one transaction. IF NOT EXISTS makes the quickstart safe to run twice.
    db.execute("CREATE TABLE IF NOT EXISTS people (id TEXT PRIMARY KEY, name TEXT NOT NULL)")

    # Insert with parameters; new_id() mints a ULID primary key.
    ada = new_id()
    db.execute("INSERT INTO people (id, name) VALUES (?, ?)", [ada, "Ada"])

    # Select into typed rows: the mapper is called with each row's columns as keyword arguments.
    @dataclass
    class Person:
        id: str
        name: str

    people = db.query("SELECT id, name FROM people WHERE id = ?", [ada], Person)
    print("query matched", people[0].id, people[0].name)

    # A batch of 1 to 256 statements runs as one transaction: all commit or none do.
    db.batch([
        ("INSERT INTO people (id, name) VALUES (:id, :name)", {"id": new_id(), "name": "Grace"}),
        ("SELECT count(*) AS n FROM people", None),
    ])
    rows = db.query("SELECT count(*) AS n FROM people")
    print("batch inserted, people count:", rows[0].get_int64("n"))

    # A refused statement raises SqlError; the whole request rolled back.
    try:
        db.execute("INSERT INTO people (id, name) VALUES (?, 'dup')", [ada])
    except SqlError as err:
        print("refused:", err.code, "statement", err.statement, "sqlstate", err.sqlstate)
```

Run it with the key exported in your shell:

```bash
python quickstart.py
```

Expected output (ids vary):

```text
query matched 01JDV7QZGK0DMB3W3QV2AB5CDM Ada
batch inserted, people count: 2
refused: primary_key_violation statement 0 sqlstate 23505
```

## How it works

- **The key never reaches TerraBase.** The client exchanges it with ts-auth for a short-lived access token, caches it and refreshes it automatically. Keep the key on your server; see [authentication](/pt-br/guides/authentication).
- **Every call is one request and one transaction.** `execute`, `query` and `batch` each post to `/sql/execute`; a batch commits all of its statements or none, and `BEGIN`/`COMMIT`/`ROLLBACK` are refused. See [SQL over HTTP](/pt-br/guides/sql-http).
- **Arguments are parameters, not string interpolation.** Positional `?` lists and named (`:name`) dicts are sent as typed JSON; `bytes` travel as base64 and integers stay exact. `Row` reads by ordinal or column name (`get_int64`, `get_string`, …).
- **Refused statements raise `SqlError`.** It carries `code` (a `SqlErrorCodes` constant), `statement` (zero-based) and `sqlstate`; the request rolled back. A `serialization_conflict` is resent at most twice; nothing else is.

## Next steps

- [SDK reference](/pt-br/guides/sdks) — the five SDKs and their install commands
- [JavaScript](/pt-br/getting-started/javascript), [C#](/pt-br/getting-started/csharp), [Go](/pt-br/getting-started/go) and [Rust](/pt-br/getting-started/rust) quickstarts
- [SQL over HTTP](/pt-br/guides/sql-http) for the raw HTTP interface and [concepts](/pt-br/concepts/sql) for the SQL dialect
- [Use SQLAlchemy and Alembic](/pt-br/guides/orm-sqlalchemy) for SQLAlchemy's SQLite dialect and Alembic migrations over the Python SDK

</div>
