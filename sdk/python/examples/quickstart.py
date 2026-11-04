"""TerraScale Python quickstart: SQL over REST, sync and async.

    export TERRASCALE_API_KEY=...        # dashboard: API keys (Full access)
    export TERRASCALE_PROJECT_ID=...     # dashboard: Organization -> Current project
    export TERRASCALE_DATABASE_NAME=main
    python quickstart.py
"""

import asyncio
import os
from dataclasses import dataclass

from terrascale import AsyncTerraScale, SqlError, SqlErrorCodes, TerraScale, new_id

DATABASE = os.environ.get("TERRASCALE_DATABASE_NAME", "main")
TABLE = "py_quickstart_" + new_id().lower()


@dataclass
class Note:
    id: str
    body: str
    blob: bytes | None


with TerraScale() as ts:
    db = ts.database(DATABASE)
    db.execute(f"CREATE TABLE {TABLE} (id TEXT PRIMARY KEY, body TEXT NOT NULL, blob BLOB)")
    first = new_id()
    db.batch([(f"INSERT INTO {TABLE} VALUES (?, ?, ?)", [first, "hello", b"\x00\x01"]),
              (f"INSERT INTO {TABLE} VALUES (:id, :body, NULL)", {"id": new_id(), "body": "world"})])
    notes = db.query(f"SELECT id, body, blob FROM {TABLE} ORDER BY body", None, Note)
    assert [n.body for n in notes] == ["hello", "world"] and notes[0].blob == b"\x00\x01", notes
    try:
        db.execute(f"INSERT INTO {TABLE} VALUES (?, 'dup', NULL)", [first])
        raise AssertionError("duplicate key accepted")
    except SqlError as err:
        assert err.code in (SqlErrorCodes.PRIMARY_KEY_VIOLATION, "sql_error"), err
        print("refused:", err.code, err.sqlstate)
    print("sync:", len(notes), "rows")


async def main() -> None:
    async with AsyncTerraScale() as ts:
        db = ts.database(DATABASE)
        rows = await db.query(f"SELECT count(*) AS n FROM {TABLE}")
        assert rows[0].get_int64("n") == 2, rows
        await db.execute(f"DROP TABLE {TABLE}")
        print("async: ok")


asyncio.run(main())
