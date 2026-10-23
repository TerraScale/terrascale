"""Smoke test: psycopg 3 against TerraBase over the Postgres wire. See ../README.md."""
import os
import secrets
import sys
from urllib.parse import unquote, urlsplit

import psycopg
from psycopg import errors

url = os.environ.get("TERRABASE_PG_URL")
if not url:
    sys.exit("set TERRABASE_PG_URL")
u = urlsplit(url)
print(f"target {u.hostname}:{u.port or 5432} db={unquote(u.path[1:])}")

t = f"smoke_py_{secrets.token_hex(4)}"
owners, items = f"{t}_owners", f"{t}_items"
insert = f"INSERT INTO {items} (sku, owner_id, qty) VALUES (%s, %s, %s)"


def check(ok, what):
    if not ok:
        raise AssertionError(f"check failed: {what}")


def count(conn):
    return conn.execute(f"SELECT count(*) FROM {items}").fetchone()[0]


def create(conn):
    conn.execute(f"CREATE TABLE {owners} (id integer PRIMARY KEY, name text NOT NULL)")
    conn.execute(f"CREATE TABLE {items} (sku text PRIMARY KEY, owner_id integer NOT NULL, qty integer)")


def insert_simple(conn):
    # ClientCursor binds client-side and sends a simple Query message.
    with psycopg.ClientCursor(conn) as cur:
        cur.execute(f"INSERT INTO {owners} (id, name) VALUES (1, 'ann'), (2, 'bob')")
        check(cur.rowcount == 2, f"rowcount={cur.rowcount}, want 2")
        cur.execute(f"INSERT INTO {items} (sku, owner_id, qty) VALUES ('a-1', 1, 3)")


def insert_params(conn):
    cur = conn.execute(insert, ("a-2", 1, 5), prepare=False)
    check(cur.rowcount == 1, f"rowcount={cur.rowcount}, want 1")


def insert_prepared(conn):
    for row in (("b-1", 2, 7), ("b-2", 2, 9)):
        cur = conn.execute(insert, row, prepare=True)
        check(cur.rowcount == 1, f"rowcount={cur.rowcount}, want 1")
    check(count(conn) == 4, "4 items")


def update(conn):
    cur = conn.execute(f"UPDATE {items} SET qty = qty + 1 WHERE owner_id = %s", (2,))
    check(cur.rowcount == 2, f"rowcount={cur.rowcount}, want 2")


def delete(conn):
    cur = conn.execute(f"DELETE FROM {items} WHERE sku = %s", ("b-2",))
    check(cur.rowcount == 1, f"rowcount={cur.rowcount}, want 1")
    check(count(conn) == 3, "3 items")


def join(conn):
    rows = conn.execute(
        f"SELECT o.name, i.sku, i.qty FROM {items} i JOIN {owners} o ON o.id = i.owner_id"
        " WHERE o.name = %s ORDER BY i.sku",
        ("ann",),
    ).fetchall()
    check(rows == [("ann", "a-1", 3), ("ann", "a-2", 5)], f"rows={rows}")


def commit(conn):
    with conn.transaction():
        conn.execute(insert, ("tx-1", 2, 1))
    check(count(conn) == 4, "committed row visible")


def rollback(conn):
    with conn.transaction():
        conn.execute(insert, ("tx-2", 2, 1))
        raise psycopg.Rollback()
    check(count(conn) == 4, "rolled back row not visible")


def unique(conn):
    try:
        conn.execute(insert, ("a-1", 1, 1))
    except errors.UniqueViolation as e:
        check(e.sqlstate == "23505", f"sqlstate={e.sqlstate}")
        return
    raise AssertionError("duplicate insert succeeded")


def prepared_select(conn):
    q = f"SELECT qty FROM {items} WHERE sku = %s"
    check(conn.execute(q, ("a-1",), prepare=True).fetchone()[0] == 3, "a-1 qty 3")
    check(conn.execute(q, ("b-1",), prepare=True).fetchone()[0] == 8, "b-1 qty 8")


steps = [
    ("create tables (integer and text primary keys)", create),
    ("insert (simple protocol, literals)", insert_simple),
    ("insert with parameters (extended protocol, unnamed statement)", insert_params),
    ("insert with a named prepared statement, reused", insert_prepared),
    ("update with affected-row count", update),
    ("delete with affected-row count", delete),
    ("select with a join and a parameter", join),
    ("transaction commit persists", commit),
    ("transaction rollback discards", rollback),
    ("unique violation is SQLSTATE 23505", unique),
    ("prepared select reused", prepared_select),
]

failed = False
try:
    conn = psycopg.connect(url, autocommit=True)
except psycopg.Error as e:
    print(f"FAIL connect: {e}")
    sys.exit(1)
with conn:
    for name, fn in steps:
        try:
            fn(conn)
            print(f"PASS {name}")
        except Exception as e:
            failed = True
            print(f"FAIL {name}: {e}")
            break
    try:
        conn.execute(f"DROP TABLE IF EXISTS {items}")
        conn.execute(f"DROP TABLE IF EXISTS {owners}")
        print("PASS drop tables")
    except Exception as e:
        failed = True
        print(f"FAIL drop tables: {e}")
print("FAIL" if failed else "PASS")
sys.exit(1 if failed else 0)
