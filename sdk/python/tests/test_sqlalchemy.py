import json
import sqlite3
from typing import Any

import httpx
import pytest
import sqlalchemy as sa
import sqlalchemy.orm as sa_orm
from fake import PROJECT
from test_sql_session import Server
from test_sql_session import serve_sql as _serve_sql_fixture
from websockets.sync.server import ServerConnection

serve_sql = _serve_sql_fixture


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
        isolation_level="AUTOCOMMIT",
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
    keyed = sa.create_engine(f"terrascale://:ts-secret-key@{PROJECT}/app")
    assert "ts-secret-key" not in str(keyed.url)
    assert "***" in str(keyed.url)
    keyed.dispose()


def test_sqlalchemy_transactions_and_savepoints_over_the_session(serve_sql: Any) -> None:
    inserted: list[list[Any]] = []
    marks: list[int] = []
    in_txn = [False]
    empty = {"columns": [], "rows": [], "rows_affected": 0, "last_insert_rowid": None}

    def answer(ws: ServerConnection, server: Server) -> None:
        while True:
            try:
                frame_id = server.recv(ws)
            except Exception:
                return
            frame = server.frames[-1]
            kind = frame["type"]
            if kind == "begin":
                in_txn[0] = True
            elif kind in ("commit", "rollback"):
                in_txn[0] = False
                if kind == "rollback":
                    inserted.clear()
                    marks.clear()
            results = []
            for stmt in frame.get("statements", []):
                sql = stmt["sql"]
                args = stmt.get("args") or []
                words = sql.strip().upper().split()
                if words[0] == "SAVEPOINT":
                    marks.append(len(inserted))
                    results.append(empty)
                elif words[0] == "RELEASE":
                    marks.pop()
                    results.append(empty)
                elif words[:2] == ["ROLLBACK", "TO"]:
                    del inserted[marks.pop() :]
                    results.append(empty)
                elif words[0] == "INSERT":
                    inserted.append(args)
                    results.append({"columns": [], "rows": [], "rows_affected": 1, "last_insert_rowid": args[0]})
                elif words[0] == "SELECT":
                    results.append(
                        {
                            "columns": [{"name": "id", "decltype": "INTEGER"}, {"name": "body", "decltype": "TEXT"}],
                            "rows": list(inserted),
                            "rows_affected": 0,
                            "last_insert_rowid": None,
                        }
                    )
                else:
                    results.append(empty)
            ws.send(json.dumps({"id": frame_id, "type": "result", "results": results, "transaction": in_txn[0]}))

    server, base = serve_sql(answer)
    engine = sa.create_engine(
        f"terrascale://{PROJECT}/app",
        connect_args={"token": "fixture-token", "region": "gru", "base_url": base},
    )
    maker = sa_orm.sessionmaker(engine)
    notes = sa.Table(
        "notes",
        sa.MetaData(),
        sa.Column("id", sa.Integer, primary_key=True),
        sa.Column("body", sa.Text),
    )
    with pytest.raises(RuntimeError):
        with maker.begin() as orm:
            orm.execute(notes.insert().values(id=10, body="lost"))
            raise RuntimeError("boom")
    with maker.begin() as orm:
        orm.execute(notes.insert().values(id=1, body="outer"))
        nested = orm.begin_nested()
        orm.execute(notes.insert().values(id=2, body="inner"))
        nested.rollback()
        rows = orm.execute(sa.text("SELECT id, body FROM notes")).fetchall()
    assert rows == [(1, "outer")]
    engine.dispose()
    assert len(server.upgrades) == 1
    frames = server.frames
    assert [frame["type"] for frame in frames] == [
        "begin",
        "execute",
        "rollback",
        "begin",
        "execute",
        "execute",
        "execute",
        "execute",
        "execute",
        "commit",
    ]
    assert frames[5]["statements"][0]["sql"].upper().startswith("SAVEPOINT")
    assert frames[7]["statements"][0]["sql"].upper().startswith("ROLLBACK TO")
    assert frames[8]["statements"][0]["sql"].upper().startswith("SELECT")
    assert inserted == [[1, "outer"]]


def test_alembic_batch_migration_adds_column_and_alters_nullability(serve_sql: Any) -> None:
    from alembic.migration import MigrationContext
    from alembic.operations import Operations

    db = sqlite3.connect(":memory:", isolation_level=None, check_same_thread=False)
    in_txn = False

    def answer(ws: ServerConnection, server: Server) -> None:
        nonlocal in_txn
        while True:
            try:
                frame_id = server.recv(ws)
            except Exception:
                return
            frame = server.frames[-1]
            if frame["type"] == "begin":
                in_txn = True
                results = []
            elif frame["type"] in ("commit", "rollback"):
                in_txn = False
                results = []
            else:
                results = [run(stmt) for stmt in frame.get("statements", [])]
            ws.send(json.dumps({"id": frame_id, "type": "result", "results": results, "transaction": in_txn}))

    def run(stmt: dict[str, Any]) -> dict[str, Any]:
        cursor = db.execute(stmt["sql"], stmt.get("args") or [])
        return {
            "columns": [{"name": d[0], "decltype": None} for d in cursor.description] if cursor.description else [],
            "rows": [list(row) for row in cursor.fetchall()],
            "rows_affected": max(cursor.rowcount, 0),
            "last_insert_rowid": cursor.lastrowid,
        }

    server, base = serve_sql(answer)
    engine = sa.create_engine(
        f"terrascale://{PROJECT}/app",
        connect_args={"token": "fixture-token", "region": "gru", "base_url": base},
    )
    with engine.begin() as conn:
        conn.execute(sa.text("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)"))
        conn.execute(sa.text("INSERT INTO notes (id, body) VALUES (1, 'a'), (2, 'b')"))
    with engine.connect() as conn:
        operations = Operations(MigrationContext.configure(conn, opts={"render_as_batch": True}))
        with operations.batch_alter_table("notes") as batch:
            batch.add_column(sa.Column("title", sa.Text))
            batch.alter_column("body", existing_type=sa.Text, nullable=False)
    with engine.connect() as conn:
        columns = list(conn.execute(sa.text("PRAGMA table_info(notes)")))
        rows = list(conn.execute(sa.text("SELECT id, body, title FROM notes ORDER BY id")))
    engine.dispose()
    db.close()
    assert [(c[1], c[2], c[3]) for c in columns] == [("id", "INTEGER", 0), ("body", "TEXT", 1), ("title", "TEXT", 0)]
    assert rows == [(1, "a", None), (2, "b", None)]
    assert any("sqlite_master" in s["sql"] for f in server.frames for s in f.get("statements", []))
    assert any(s["sql"].startswith("PRAGMA table_info") for f in server.frames for s in f.get("statements", []))
