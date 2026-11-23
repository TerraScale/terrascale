# Python

Use Python 3.10+ and choose `TerraScale` for sync or `AsyncTerraScale` for
asyncio. The package is pending PyPI publication. Sources:
[Python quickstart](https://docs.terrascale.tech/getting-started/python),
[SDK reference](https://docs.terrascale.tech/guides/sdks).

## Install and configure

```sh
python -m venv .venv
. .venv/bin/activate
python -m pip install "git+https://github.com/TerraScale/terrascale@main#subdirectory=sdk/python"
```

Record or pin the source revision for reproducibility. `TerraScale()` reads
`TERRASCALE_API_KEY`, `TERRASCALE_PROJECT_ID`, optional `TERRASCALE_BASE_URL`.
Explicit fields are `api_key`, `project`, `token`, `base_url`, `region`,
`issuer` and `timeout`. Supply `api_key` or `token`, not both.
With a custom `base_url` and an API key, pass the intended ts-auth issuer
as `issuer=`, including `/terrascale`. `region="gru"` pins the entry region;
it does not read `TERRASCALE_REGION` for you.
Sources: [quickstart](https://docs.terrascale.tech/getting-started/python),
[authentication](https://docs.terrascale.tech/guides/authentication),
[regions](https://docs.terrascale.tech/concepts/regions).

## Complete small example

Provide the three environment variables from the skill, save as `quickstart.py`
and run `python quickstart.py`. The callback runs inside one WebSocket transaction.

```python
import os
from dataclasses import dataclass

from terrascale import SqlError, TerraScale, new_id


@dataclass
class Person:
    id: str
    name: str


with TerraScale(timeout=30.0) as client:
    db = client.database(os.environ["TERRASCALE_DATABASE_NAME"])
    db.execute("CREATE TABLE IF NOT EXISTS sdk_people (id TEXT PRIMARY KEY, name TEXT NOT NULL)")
    person_id = new_id()
    db.batch([
        ("INSERT INTO sdk_people (id, name) VALUES (?, ?)", [person_id, "Ada"]),
        ("INSERT INTO sdk_people (id, name) VALUES (:id, :name)", {"id": new_id(), "name": "Grace"}),
    ])
    people = db.query("SELECT id, name FROM sdk_people WHERE id = ?", [person_id], Person)
    print(people[0].name)

    def rename(tx):
        rows = tx.query("SELECT name FROM sdk_people WHERE id = ?", [person_id])
        if rows[0].get_string("name") == "Ada":
            tx.execute("UPDATE sdk_people SET name = ? WHERE id = ?", ["Ada Lovelace", person_id])

    with db.connect() as session:
        session.transaction(rename)

    try:
        db.execute("INSERT INTO sdk_people (id, name) VALUES (?, ?)", [person_id, "duplicate"])
    except SqlError as error:
        if error.code != "primary_key_violation":
            raise
        print(error.code, error.statement, error.sqlstate)
```

Expected: `Ada`, then `primary_key_violation 0 23505`. `query(..., Person)`
calls `Person(**row)`, so selected column names must match the constructor.
Sources: [Python](https://docs.terrascale.tech/getting-started/python),
[SQL HTTP](https://docs.terrascale.tech/guides/sql-http).

## Async form and result handling

For asyncio, use the exported `AsyncTerraScale`; database operations are awaited:

```python
import os
from terrascale import AsyncTerraScale

async def first_query():
    async with AsyncTerraScale() as client:
        db = client.database(os.environ["TERRASCALE_DATABASE_NAME"])
        rows = await db.query("SELECT ? AS value", [42])
        return rows[0].get_int64("value")
```

Use `async with db.connect()` and an async transaction callback for an async
session. `timeout=` configures httpx transport timeouts; asyncio task cancellation
interrupts async work. There is no per-call Go-style context or .NET-style
cancellation-token parameter on Python SQL methods. A timeout does not prove
that a write failed. Sources: [SDK reference](https://docs.terrascale.tech/guides/sdks),
[errors](https://docs.terrascale.tech/guides/errors).

`execute(sql, args=None)` and `execute(Statement(...))` return `StatementResult`;
`batch` accepts SQL strings, `(sql, args)` pairs or `Statement` objects.
Without a mapper, `query` returns `Row` objects supporting ordinal/name access
and `get_int64`, `get_string`, `get_bytes`, `is_null`. Arguments are lists or
dicts; integers remain exact and blobs are `bytes`.

`SqlError` includes `code`, `statement`, `sqlstate`, `status`, `is_transient`.
Catch `APIError` for non-SQL HTTP refusals, including `retry_after` when supplied.
Lost committing session replies produce `CommitUncertainError`; the session
stays unusable. If an open transaction was lost without a committing request,
`rollback()` clears local lost-transaction state before reconnection.
Sources: [Python](https://docs.terrascale.tech/getting-started/python),
[safe retries](https://docs.terrascale.tech/guides/errors).
Source audit: `sdk/python/README.md`, `src/terrascale/__init__.py`, `client.py`,
`aio.py`, `session.py`, `sql.py`, `errors.py` at the revision in [the skill](../SKILL.md).
