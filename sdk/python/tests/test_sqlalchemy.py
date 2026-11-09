import json
from typing import Any

import httpx
import sqlalchemy as sa
from fake import PROJECT


def test_sqlalchemy_engine_round_trip() -> None:
    rows: dict[int, str] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        body = json.loads(request.content)
        results = []
        for stmt in body["statements"]:
            sql = stmt["sql"]
            args = stmt.get("args") or []
            head = sql.lstrip().split(None, 1)[0].upper()
            if head == "CREATE" or head == "PRAGMA" or "sqlite_master" in sql:
                results.append({"columns": [], "rows": [], "rows_affected": 0, "last_insert_rowid": None})
            elif head == "INSERT":
                row_id = args[0]
                rows[row_id] = args[1]
                results.append({"columns": [], "rows": [], "rows_affected": 1, "last_insert_rowid": row_id})
            elif head == "SELECT":
                selected = [[row_id, body] for row_id, body in sorted(rows.items())]
                results.append(
                    {
                        "columns": [{"name": "id", "decltype": "INTEGER"}, {"name": "body", "decltype": "TEXT"}],
                        "rows": selected,
                        "rows_affected": 0,
                        "last_insert_rowid": None,
                    }
                )
            else:
                results.append({"columns": [], "rows": [], "rows_affected": 0, "last_insert_rowid": None})
        return httpx.Response(200, json={"results": results})

    http = httpx.Client(transport=httpx.MockTransport(handler))
    engine = sa.create_engine(
        f"terrascale://{PROJECT}/app",
        connect_args={"token": "fixture-token", "region": "gru", "http_client": http},
    )
    metadata = sa.MetaData()
    notes = sa.Table(
        "notes",
        metadata,
        sa.Column("id", sa.Integer, primary_key=True),
        sa.Column("body", sa.Text),
    )
    metadata.create_all(engine)
    with engine.connect() as conn:
        inserted = conn.execute(notes.insert(), [{"id": 1, "body": "a"}, {"id": 2, "body": "b"}])
        assert inserted.rowcount == 2
        selected = conn.execute(sa.select(notes.c.id, notes.c.body).order_by(notes.c.id))
        assert selected.fetchall() == [(1, "a"), (2, "b")]
    raw: Any = engine.raw_connection()
    cursor = raw.cursor()
    cursor.execute("INSERT INTO notes (id, body) VALUES (?, ?)", [3, "c"])
    assert cursor.rowcount == 1
    assert cursor.lastrowid == 3
    cursor.execute("SELECT id, body FROM notes ORDER BY id")
    assert cursor.fetchall() == [(1, "a"), (2, "b"), (3, "c")]
    assert cursor.description[0][0] == "id"
    cursor.close()
    raw.close()
    engine.dispose()
    http.close()
