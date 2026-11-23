# Worked migration: owner-scoped todos

This example has exactly five columns: `id uuid`, `owner_id uuid`, `title text`,
`done boolean`, `created_at timestamptz`, all NOT NULL. IDs have no dependencies on
other app tables here. Add every actual column and relationship to the conversion
map before using this on another schema. Keep source sign-in during this database
slice and retain owner UUIDs as text. A later identity migration must reconcile
its subject mapping separately.

Source commands use configured libpq environment variables; see
[psql](https://www.postgresql.org/docs/current/app-psql.html).
Target SQL and commands follow [SQL](https://docs.terrascale.tech/concepts/sql),
[import](https://docs.terrascale.tech/guides/import) and
[CLI](https://docs.terrascale.tech/cli).

## 1. Export canonical source rows

Fence source writers first. Run this command from the private migration directory.
The SELECT runs against source PostgreSQL, never TerraScale. It
normalizes UUIDs, UTC microseconds and booleans before emitting one JSON object
per row. The psql unaligned output keeps JSON escapes intact.

```sh
psql -X -qAt -v ON_ERROR_STOP=1 > todos.jsonl <<'SQL'
SELECT json_build_object(
  'id', id::text,
  'owner_id', owner_id::text,
  'title', title,
  'done', CASE WHEN done THEN 1 ELSE 0 END,
  'created_at', to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')
)::text
FROM public.todos ORDER BY id;
SQL
```

Check the source columns have the declared NOT NULL constraints first. A nullable
boolean needs a separate NULL branch instead of the example's `CASE` expression.
For large datasets, export bounded stable primary-key ranges at the same fence;
do not silently truncate or use a source query result as the destination limit.

## 2. Convert rows and define schema

Save as `convert-todos.py` outside the application checkout. It rejects unexpected
columns, nulls and representations. It handles quotes/newlines through SQLite
literal encoding and writes data to files, never terminal output. Python sqlite3
is used only as a local value encoder, without making any claim about TerraBase
internals. Source: [Python sqlite3](https://docs.python.org/3/library/sqlite3.html).

```python
import hashlib
import json
import re
import sqlite3
from pathlib import Path
from uuid import UUID

columns = ["id", "owner_id", "title", "done", "created_at"]
encoder = sqlite3.connect(":memory:")
with Path("todos.jsonl").open() as source, Path("data.sql").open("w") as output, Path("source-samples.sha256").open("w") as hashes:
    for index, line in enumerate(source):
        row = json.loads(line)
        if set(row) != set(columns):
            raise ValueError("unexpected columns")
        for key in ["id", "owner_id"]:
            if not isinstance(row[key], str) or str(UUID(row[key])) != row[key]:
                raise ValueError("noncanonical UUID")
        if not isinstance(row["title"], str) or "\x00" in row["title"]:
            raise ValueError("invalid text")
        if type(row["done"]) is not int or row["done"] not in [0, 1]:
            raise ValueError("invalid boolean")
        if not isinstance(row["created_at"], str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z", row["created_at"]):
            raise ValueError("timestamp needs explicit mapping")
        values = [row[key] for key in columns]
        literals = [encoder.execute("SELECT quote(?)", (value,)).fetchone()[0] for value in values]
        statement = "INSERT INTO todos (id,owner_id,title,done,created_at) VALUES (" + ",".join(literals) + ");\n"
        if len(statement.encode()) > 16384:
            raise ValueError("use bound HTTP parameters for oversize SQL text")
        output.write(statement)
        if index < 20:
            canonical = json.dumps(values, ensure_ascii=True, separators=(",", ":"))
            hashes.write(hashlib.sha256(canonical.encode()).hexdigest() + "\n")
encoder.close()
```

```sh
python3 convert-todos.py
```

Write this as `schema.sql`, or the tracked baseline migration described in the
skill. Defaults are application-supplied to preserve a single timestamp format:

```sql
CREATE TABLE todos (
  id TEXT PRIMARY KEY NOT NULL,
  owner_id TEXT NOT NULL,
  title TEXT NOT NULL,
  done INTEGER NOT NULL,
  created_at TEXT NOT NULL
);
CREATE INDEX todos_owner_id ON todos (owner_id);
```

Validate boolean values in every writer; the source converter is not ongoing
validation. Import schema via either migration or import, then import `data.sql`
as the skill describes. No source RLS statement is included.

## 3. Verify counts, hashes and queries

Run counts for each table in the inventory, substituting its audited names:

```sh
psql -X -At -v ON_ERROR_STOP=1 -c 'SELECT count(*) FROM public.todos' > source-count.txt
ts -project "$TERRASCALE_PROJECT_ID" sql app-rehearsal 'SELECT count(*) AS n FROM todos' > target-count.jsonl
ts -project "$TERRASCALE_PROJECT_ID" sql app-rehearsal 'SELECT id, owner_id, title, done, created_at FROM todos ORDER BY id LIMIT 20' > target-samples.jsonl
ts -project "$TERRASCALE_PROJECT_ID" sql app-rehearsal 'PRAGMA foreign_key_check' > foreign-key-check.jsonl
ts -project "$TERRASCALE_PROJECT_ID" sql app-rehearsal 'SELECT done, count(*) AS n FROM todos GROUP BY done ORDER BY done' > target-groups.jsonl
```

CLI reads are JSON lines, documented in
[migrations, read tables](https://docs.terrascale.tech/guides/migrations#read-tables).
Compute the same canonical sample hashes and compare counts without exposing data:

```sh
python3 - <<'PY'
import hashlib
import json
from pathlib import Path

source_count = int(Path("source-count.txt").read_text().strip())
target_count = json.loads(Path("target-count.jsonl").read_text())["n"]
if source_count != target_count:
    raise SystemExit("row count mismatch")
columns = ["id", "owner_id", "title", "done", "created_at"]
hashes = []
for line in Path("target-samples.jsonl").read_text().splitlines():
    row = json.loads(line)
    canonical = json.dumps([row[key] for key in columns], ensure_ascii=True, separators=(",", ":"))
    hashes.append(hashlib.sha256(canonical.encode()).hexdigest())
if hashes != Path("source-samples.sha256").read_text().splitlines():
    raise SystemExit("sample checksum mismatch")
if Path("foreign-key-check.jsonl").read_text().strip():
    raise SystemExit("foreign key violation")
print("Counts, canonical sample checksums and foreign keys match.")
PY
```

Compare grouped counts with the equivalent source query, exercise owner-scoped
pagination and inspect boundary rows separately. Twenty ordered samples are a
spot check, not a full data-integrity proof; select extra fixed IDs throughout
the keyspace and around every representation boundary. Destination result and
scan limits require bounded keyset queries for large verification jobs:
[limits](https://docs.terrascale.tech/limits).

## 4. Replace auth.uid() policies with server authorization

A typical source policy uses `USING (auth.uid() = owner_id)` and
`WITH CHECK (auth.uid() = owner_id)` for owner-scoped access. Supabase explains
how these protect existing and proposed rows in its
[RLS guide](https://supabase.com/docs/guides/database/postgres/row-level-security).

In this worked path, all requests go through an authenticated backend. The backend
validates the trusted identity provider's signature, issuer, audience and expiry,
then resolves `(issuer, subject)` to `legacy_owner_id` using a server-controlled
mapping. Never accept the effective owner ID from request JSON or a query string;
never link identities by unverified email. Keep the project SQL credential on
the server, with no privileged SQL endpoint exposed to users.

Using the driver's bound parameters, execute these SQLite statements. Bind the
owner parameter from that verified mapping:

```sql
SELECT id, title, done, created_at
FROM todos WHERE owner_id = ? AND id = ?;
INSERT INTO todos (owner_id, id, title, done, created_at)
VALUES (?, ?, ?, ?, ?);
UPDATE todos SET title = ?, done = ?
WHERE owner_id = ? AND id = ?;
DELETE FROM todos WHERE owner_id = ? AND id = ?;
```

For UPDATE the mapping binds parameter 3, not parameter 1; values 1/2 are validated
title/done input. For INSERT generate ID and UTC timestamp on the server; do not
allow a payload owner field to overwrite the mapped value. Return a generic
not-found/denied result for zero matching rows. Keep ownership immutable in this
endpoint; a separate transfer operation requires its own authorization and audit.
Apply the same predicates to joins, bulk changes, background jobs and search.

Test anonymous requests fail before SQL, user A can CRUD A's task, user B cannot
read/update/delete A's task, and changing a submitted owner ID cannot bypass the
mapping. A service-key query succeeding says nothing about user isolation.

For direct shape/row reads later, follow
[data access for users](https://docs.terrascale.tech/guides/end-user-authentication#data-access-for-your-users):
map the identity to a canonical ULID, mint a short-lived token on the backend using
a project key with `auth:tokens:mint`, and configure verified database rules before
exposing reads. A token's table-name list is ignored by TerraBase. This does not
translate `auth.uid()` or establish write-policy equivalence. Use the linked Rules
contract for exact rule syntax and prove two-user denial against the deployed
project. Follow the precedence warning in
[authentication capabilities](https://docs.terrascale.tech/guides/authentication-capabilities)
when older pages conflict about project OIDC tokens. Until verified, retain this
backend path for CRUD rather than exposing privileged keys or guessing rules.
