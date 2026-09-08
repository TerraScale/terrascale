import os
import sqlite3
import tempfile

OLD = """SELECT COALESCE(MAX(seq), 0) + 1 FROM (
 SELECT origin_seq AS seq FROM documents WHERE origin = ?1 AND tenant = ?2
 UNION ALL SELECT seq FROM origin_prefixes WHERE origin = ?1 AND tenant = ?2
 UNION ALL SELECT origin_seq FROM replication_publications WHERE origin = ?1 AND tenant = ?2)"""
NEW = """SELECT COALESCE(MAX(seq), 0) + 1 FROM (
 SELECT MAX(origin_seq) AS seq FROM documents WHERE origin = ?1 AND tenant = ?2
 UNION ALL SELECT MAX(seq) FROM origin_prefixes WHERE origin = ?1 AND tenant = ?2
 UNION ALL SELECT MAX(origin_seq) FROM replication_publications WHERE origin = ?1 AND tenant = ?2)"""


def fixture(rows):
    fd, path = tempfile.mkstemp(prefix="origin-max-", suffix=".db")
    os.close(fd)
    db = sqlite3.connect(path)
    db.executescript("""CREATE TABLE documents (
        tenant TEXT NOT NULL, collection TEXT NOT NULL, id TEXT NOT NULL,
        commit_seq INTEGER NOT NULL, commit_ulid TEXT NOT NULL,
        revision TEXT NOT NULL, requested INTEGER NOT NULL, digest INTEGER NOT NULL,
        version INTEGER NOT NULL, deleted INTEGER NOT NULL, committed_at INTEGER NOT NULL,
        body BLOB NOT NULL, kind TEXT NOT NULL DEFAULT 'origin', origin TEXT NOT NULL DEFAULT '',
        origin_seq INTEGER NOT NULL DEFAULT 0, hlc INTEGER NOT NULL DEFAULT 0,
        context BLOB NOT NULL DEFAULT X'', expires_at INTEGER,
        client_request_ulid TEXT NOT NULL DEFAULT '',
        seq INTEGER GENERATED ALWAYS AS (commit_seq) VIRTUAL,
        dot TEXT GENERATED ALWAYS AS (origin || ':' || origin_seq) VIRTUAL,
        PRIMARY KEY (tenant, collection, id, commit_seq)) WITHOUT ROWID;
        CREATE INDEX documents_origin_frontier ON documents (tenant, origin, origin_seq, hlc);
        CREATE TABLE origin_prefixes (
          tenant TEXT NOT NULL, origin TEXT NOT NULL, seq INTEGER NOT NULL CHECK (seq > 0),
          hlc INTEGER NOT NULL, PRIMARY KEY (tenant, origin)) WITHOUT ROWID;
        CREATE TABLE replication_publications (
          tenant TEXT NOT NULL, origin TEXT NOT NULL, origin_seq INTEGER NOT NULL,
          hlc INTEGER NOT NULL, operation TEXT NOT NULL, local_seq INTEGER NOT NULL,
          committed_at INTEGER NOT NULL, context BLOB NOT NULL, private_payload BLOB NOT NULL,
          PRIMARY KEY (tenant, origin, origin_seq), UNIQUE (tenant, local_seq)) WITHOUT ROWID;
    """)
    for i in range(1, rows + 1):
        db.execute("""INSERT INTO documents
          (tenant,collection,id,commit_seq,commit_ulid,revision,requested,digest,version,
           deleted,committed_at,body,origin,origin_seq,hlc)
          VALUES ('t','c',?,?,?,'r',0,0,1,0,0,X'', 'o', ?, ?)""", (f"d{i}", i, f"u{i}", i, i))
    db.execute("INSERT INTO origin_prefixes VALUES ('t','o',?,?)", (rows + 100, rows + 100))
    for i in range(rows + 1, rows * 2 + 1):
        db.execute("INSERT INTO replication_publications VALUES ('t','o',?,?,?, ?,0,X'',X'')", (i, i, f"op{i}", i))
    db.execute("INSERT INTO replication_publications VALUES ('t','other',999999,'x', 'other',1,0,X'',X'')")
    db.execute("INSERT INTO replication_publications VALUES ('other','o',999999,'x', 'other',1,0,X'',X'')")
    db.commit()
    return db, path


for rows in (1000, 100000):
    db, path = fixture(rows)
    print(f"rows_per_large_branch={rows}")
    for label, query in (("old", OLD), ("new", NEW)):
        plan = [r[3] for r in db.execute("EXPLAIN QUERY PLAN " + query, ("o", "t"))]
        steps = 0

        def tick():
            nonlocal_steps[0] += 1

        nonlocal_steps = [0]
        db.set_progress_handler(tick, 1)
        result = db.execute(query, ("o", "t")).fetchone()[0]
        db.set_progress_handler(None, 0)
        opcodes = len(list(db.execute("EXPLAIN " + query, ("o", "t"))))
        print(label, "result", result, "vm_steps", nonlocal_steps[0], "opcodes", opcodes, "plan", plan)
    print("empty", db.execute(NEW, ("missing", "t")).fetchone()[0])
    print("other_origin", db.execute(NEW, ("other", "t")).fetchone()[0])
    print("wrong_tenant", db.execute(NEW, ("o", "missing")).fetchone()[0])
    db.close()
    os.unlink(path)
