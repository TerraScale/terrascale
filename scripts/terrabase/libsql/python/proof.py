"""Proof: libsql (Python) against TerraBase Hrana over HTTP. See ../README.md."""
import os
import secrets
import sys

import libsql

url, token_file = os.environ.get("LIBSQL_URL"), os.environ.get("LIBSQL_TOKEN_FILE")
if not url or not token_file:
    sys.exit("set LIBSQL_URL and LIBSQL_TOKEN_FILE")
with open(token_file) as f:
    conn = libsql.connect(url, auth_token=f.read().strip())

t = f"proof_py_{secrets.token_hex(4)}"
owners, items = f"{t}_owners", f"{t}_items"


def check(ok, what):
    if not ok:
        raise AssertionError(f"check failed: {what}")


def count():
    return conn.execute(f"SELECT count(*) FROM {items}").fetchone()[0]


failed = False
try:
    # The module follows sqlite3: DML opens a transaction implicitly; commit() or rollback() ends it.
    print(f"create tables {owners}, {items}")
    conn.execute(f"CREATE TABLE {owners} (id INTEGER PRIMARY KEY, name TEXT NOT NULL)")
    conn.execute(f"CREATE TABLE {items} (sku TEXT PRIMARY KEY, owner_id INTEGER NOT NULL, qty INTEGER)")

    cur = conn.execute(f"INSERT INTO {owners} (id, name) VALUES (?, ?)", (1, "ann"))
    conn.execute(f"INSERT INTO {owners} (id, name) VALUES (?, ?)", (2, "bob"))
    conn.commit()
    print(f"insert with parameters: rowcount={cur.rowcount}")
    check(cur.rowcount == 1, "insert affected 1 row")

    conn.executemany(
        f"INSERT INTO {items} (sku, owner_id, qty) VALUES (?, ?, ?)",
        [("a-1", 1, 3), ("a-2", 1, 5), ("b-1", 2, 7)],
    )
    conn.commit()
    print("batch (executemany, one transaction): 3 statements")

    rows = conn.execute(
        f"SELECT o.name, i.sku, i.qty FROM {items} i JOIN {owners} o ON o.id = i.owner_id ORDER BY i.sku"
    ).fetchall()
    print("select with join:", ", ".join(f"{n}/{s}/{q}" for n, s, q in rows))
    check(len(rows) == 3 and rows[2][0] == "bob", "join returned 3 rows, last owned by bob")

    for sku, commit in (("tx-1", True), ("tx-2", False)):
        conn.execute(f"INSERT INTO {items} (sku, owner_id, qty) VALUES (?, 2, 1)", (sku,))
        check(conn.in_transaction, "insert opened a transaction")
        seen = conn.execute(f"SELECT count(*) FROM {items} WHERE sku = ?", (sku,)).fetchone()[0]
        check(seen == 1, "transaction sees its own insert")
        conn.commit() if commit else conn.rollback()
        print(f"transaction {'commit' if commit else 'rollback'}: count={count()}")
        check(count() == 4, "count is 4 after commit and after rollback")
except Exception as e:
    failed = True
    print(f"error: {e}")
    if conn.in_transaction:
        conn.rollback()
finally:
    try:
        conn.execute(f"DROP TABLE IF EXISTS {items}")
        conn.execute(f"DROP TABLE IF EXISTS {owners}")
        conn.commit()
        print("drop tables")
    except Exception as e:
        failed = True
        print(f"drop failed: {e}")
print("FAIL" if failed else "PASS")
sys.exit(1 if failed else 0)
