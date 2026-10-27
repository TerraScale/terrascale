# Relational SQL and libSQL (Hrana) over the document lattice (SQ1)

- Status: design, 2026-10-22, SQ1 owner. It extends [R9](../r9-sql-writes-design.md) from single-collection writes to
  tables, joins and transactions, and adds Hrana-over-HTTP for the official libSQL clients.
- Shared path: the HTTP SQL API, Hrana and the PostgreSQL wire adapter (PG1) all call `ISqlService`
  (`src/TerraBase/Features/Sql/`). No front end parses or executes SQL on its own.
- The dashboard route `POST /v1/projects/{project}/databases/{database}/sql` keeps its read-only single-collection
  subset and its `{id, value}` rows. It is unchanged.

## 1. Model

| SQL | TerraBase |
| --- | --- |
| table `t` | collection `t` (same name rules: `^[a-z][a-z0-9_-]{0,62}$`, unquoted SQL names are lowercased) |
| row | one document |
| column | one top-level field of the document; SQL `NULL` is a missing field |
| table definition (`CREATE TABLE`) | member `sql` of the collection's `_collections` metadata document, so it replicates as catalog state |
| primary key | the document id (section 3) |

Every row write is a `DocumentOperation` applied with `IDocumentService.ApplyAsync` in one storage write transaction,
so the CDC tailer ships it, peers join it, and GC, refill and anti-entropy treat it like a document-API write. Nothing
below the document service learns about SQL. Collections without a `sql` member are not visible to relational SQL.

## 2. Statements

Parsed with SqlParserCS (SQLite dialect). Every statement is validated on the syntax tree and rendered back from the
tree, so the engine never sees text the validator did not see.

| Statement | Translation |
| --- | --- |
| `SELECT`, `VALUES`, `WITH … SELECT` (joins, subqueries, aggregates, `GROUP BY`, window functions, `UNION`, `LIMIT/OFFSET`) | one engine read (section 4) |
| `INSERT INTO t [(cols)] VALUES … \| SELECT …` | evaluate the rows with one engine read, then a create-only `put` per row (`Precondition.Absent`) |
| `INSERT OR REPLACE`, `REPLACE` | delete documents holding conflicting UNIQUE keys, then an unconditional `put` ([conflict compatibility](conflict-compatibility.md)) |
| `INSERT OR IGNORE`, `ON CONFLICT [(cols)] DO NOTHING` | uniqueness lookup, then a create-only `put`; skipped rows follow [conflict compatibility](conflict-compatibility.md) |
| `UPDATE t SET … WHERE …` | engine read `SELECT <key>, <new values> FROM t WHERE …`, then a `patch` per row: `c = NULL` → `unset`; `c = c ± <integer literal>` on an `INTEGER NOT NULL` column → `inc` (counters add across regions), on any other column the computed value (NULL stays NULL); anything else → `set` (per-field last-writer-wins) |
| `DELETE FROM t WHERE …` | engine read of the keys, then a `delete` per row |
| `CREATE TABLE [IF NOT EXISTS]` | create the collection and write its definition |
| `DROP TABLE [IF EXISTS]` | drop the collection |
| `CREATE [UNIQUE] INDEX`, `DROP INDEX` | the index service (unique: the unique-constraint service) |
| `BEGIN [DEFERRED\|IMMEDIATE\|EXCLUSIVE]`, `BEGIN [TRANSACTION] READONLY`, `COMMIT`/`END`, `ROLLBACK` | the session (section 5) |
| `INSERT`/`UPDATE`/`DELETE … RETURNING` columns, `*` or `column AS name` | the written rows: new values, old ones for a delete |
| `PRAGMA foreign_keys = …`, `PRAGMA read_uncommitted`, `PRAGMA [main.\|temp.]table_info(t)` | no-op, `0`, SQLite's column rows (ORMs and libSQL clients send them) |

Refused with an error that never quotes client text: `RETURNING` expressions, `ON CONFLICT DO UPDATE`, `OR FAIL`/`OR ROLLBACK`, `ALTER TABLE`,
triggers, views, other `PRAGMA`s, `ATTACH`, `VACUUM`, savepoints, schemas other than `main` (`main.t` is `t`), every table reference that is not a SQL table
of the database or a CTE of the statement, and every function outside the allow-list (core scalar, aggregate, window,
date/time and JSON functions of SQLite; no table-valued functions). Blob values are refused: a JSON document cannot
hold bytes.

Only plain and `"…"` names, `` `…` `` names without quote characters (rewritten as `"…"`; Drizzle and Prisma quote with
backticks) and only `'…'` string literals are accepted (SQ1-e4): SqlParserCS renders `[a]]b]` and `N'a''b'` back
without escaping, which made SQLite read other text than the validator saw. Literals in expressions are
numbers, strings, NULL, booleans and parameters. The rendered text must parse back to itself, and the engine refuses a
program whose text holds more than one statement (Turso runs every statement of a command). `printf`, `format` and
`replace` are not in the allow-list: nested calls build unbounded strings.

Column types follow SQLite affinity on write (`INTEGER`, `REAL`, `TEXT`, `NUMERIC`, none). `NOT NULL`, literal
`DEFAULT`, `PRIMARY KEY` and `UNIQUE` (through the unique-constraint service) are enforced; `CHECK` is refused, and
`REFERENCES`/`FOREIGN KEY` are accepted and not enforced, as in SQLite with foreign keys off (its default). A single
`INTEGER` or `SERIAL` primary key left out of an `INSERT` is generated.

## 3. Keys

- Single-column primary key: `INTEGER` → canonical decimal (`42`), `TEXT` → the text. Users choose their keys; nothing
  forces ULIDs (id contract: 1–256 characters, UTF-8, no control characters).
- Composite primary key, or a single key column of another affinity (none, `NUMERIC`, `REAL`): the JSON array of the
  key values (`[1,"a"]`, `[1]`, `["1"]`), so `1` and `'1'` stay different keys where SQLite keeps them apart (SQ1-e6).
- No primary key: a generated ULID, invisible to SQL.
- `INTEGER PRIMARY KEY` omitted on insert: a generated positive integer below 2^53 (milliseconds since 2026-01-01
  times 4096 plus 12 random bits). A local collision retries; two regions inserting the same generated key in the same
  millisecond merge per field (documented limit of a leaderless store). It is the statement's `last_insert_rowid`.
- Key columns are also stored as fields, so every column reads the same way.

## 4. Engine reads

`IStorageTransaction.ExecuteSqlAsync(SqlProgram, CancellationToken)` (engine-neutral types in `Engine/`, the Turso
implementation in `Engine/Sqlite/`) runs one statement:

```
WITH "t" AS (SELECT json_extract(doc, '$."c1"') AS "c1", … FROM main."t" WHERE tb_qos_scan(octet_length(doc))), …
<statement rendered from the validated tree>
```

- One CTE per SQL table the statement references; `main."t"` names the physical table, the CTE shadows it.
- Arguments are bound parameters (`?`, `?NNN`, `:name`, `@name`, `$name`); values are typed (null, integer, real, text).
- Bounds: the existing work budget (5 million units or 5 s, `QueryCapacityException` → "query too expensive"),
  at most 10,000 rows and 8 MiB per result. Past either bound a statement fails with `SQLITE_TOOBIG` (DECISION, SQ1-c:
  reads too, not a `truncated` flag, because neither `SqlResult` nor Hrana can carry one and silently dropped rows are wrong).
- `WITH` only at the start of a statement, and not `RECURSIVE` (DECISION, SQ1-c: rows a recursive CTE generates pass no
  table scan, so the work budget would never charge them).
- Parameters are renumbered `$p1`, `$p2`, … in SQLite's order (`?` = largest so far + 1, `?NNN`, names on first use) before
  parsing; positional arguments bind by number, named arguments (with or without prefix) by name, unbound ones are NULL.
- shortcut: CTEs are not flattened by Turso, so every read scans each referenced table and no index serves it; the
  work budget caps a read at roughly 150,000 rows. Upgrade to column rewriting over `json_extract` (index-served) when a
  customer table passes that.
- Join fan-out: every SELECT of a statement (top level, subqueries, CTE bodies, both sides of a compound) gets
  `WHERE tb_sql_row(coalesce(<a column of each FROM item>, 0)) AND (…)`, which charges one work unit per evaluated row
  combination (Turso evaluates a function of constants once, so the argument names every joined item). A joined
  subquery or CTE whose first column has no name is refused (SQ1-e3). The guard runs in SELECTs over CTEs and
  `VALUES` too, and checks the deadline. Besides each statement's budget, all statements of one request (a replay
  included) share a 5 s deadline, so a write request cannot hold the writer for 256 budgets (SQ1-e5).

## 5. Transactions

- Autocommit statements run alone: a read in a read transaction, a write in its own write transaction.
- A transaction (`BEGIN` … `COMMIT`) inside one request runs in one write transaction, committed at `COMMIT`.
- A transaction that spans requests (Hrana baton) never holds the database writer between requests. The session keeps
  a log of the statements, their arguments, generated values and a hash of each result. The next request opens a write
  transaction, replays the log, checks every hash, runs its own statements and rolls back. `COMMIT` replays and keeps.
  A hash mismatch is a conflict: the commit fails with `SQLITE_BUSY` "transaction conflict: data changed since the
  transaction read it; retry the transaction", and nothing commits (optimistic, serializable).
  Limits: 256 statements, 1 MiB of log, 1024 rows written (one change group, as any transaction), 30 s idle.
- Statements using `random()` or the current time inside a multi-request transaction can conflict at `COMMIT`; retry.
- A statement that fails inside a transaction undoes only itself, as in SQLite: the request's storage transaction is
  dropped (a refused document write may leave it uncommittable) and the next statement replays the log without it. The
  transaction stays open for more statements, `COMMIT` or `ROLLBACK`. A failed `COMMIT` (a conflict) ends it; `ROLLBACK`
  without a transaction is a no-op.
- `SqlError.Kind` is a stable sub-code (`no_such_table`, `no_such_column`, `already_exists`, `syntax`, `invalid_input`,
  `constraint`, `conflict`, `auth`, `too_big`, `interrupt`); `ISqlService.ListTablesAsync` lists tables with their
  columns for front ends (PG1 catalog queries).
- `ISqlSession.ExecuteScriptAsync` (added for Hrana `sequence`) splits text with the same parser and runs each
  statement without arguments; `BEGIN READONLY` is not available inside a script.
- `…/sql/execute` wraps its statements in `BEGIN` … `COMMIT` (`BEGIN READONLY` when none writes, so reads never take the
  writer) and refuses transaction statements in the body.
- Sessions live in memory on the node that opened them, bounded (count and bytes); a restart or an expired baton
  answers "stream not found".

## 6. Front ends

### HTTP SQL API

`POST /v1/projects/{project}/databases/{database}/sql/execute`, client listener, project data API key:

```json
{"statements": [{"sql": "INSERT INTO t (id, name) VALUES (?, ?)", "args": [1, "ann"]},
                {"sql": "SELECT * FROM t WHERE id = :id", "named": {"id": 1}}]}
```

All statements run as one transaction (all or nothing). Response:
`{"results": [{"columns": [...], "rows": [[...]], "rows_affected": 0, "last_insert_rowid": null}]}`.
Errors: `400 sql_error` with `{"error": {"code", "message", "statement", "sqlite_code"}}`, `403 forbidden_scope`
(`SQLITE_AUTH`), `409 sql_conflict`, `413 sql_too_large`. `sqlite_code` carries the `SqlError` code (DECISION, SQ1-b:
the HTTP code stays the API's own vocabulary; clients that map SQLite codes read `sqlite_code`).

### Hrana over HTTP (libSQL clients)

Base URL `https://api.terrascale.tech/v1/projects/{project}/databases/{database}/libsql/` (the trailing slash matters
for @libsql/client, which resolves `v2/pipeline` relative to it). `authToken` is a project data API key.

- `POST …/libsql/v2/pipeline` and `POST …/libsql/v3/pipeline` (one JSON handler), `POST …/libsql/v3/cursor`
  (newline-delimited JSON). @libsql/client speaks v2 only; the Rust crate and the Go and Python clients built on it
  speak v3 only and need the cursor. WebSocket and protobuf are not served: no official client needs them for
  `https://` or `libsql://` URLs (DECISION, research of client sources and a mock run, 2026-10-22).
- Requests: `execute`, `batch` (conditions `ok`, `error`, `not`, `and`, `or`, `is_autocommit`), `sequence`,
  `describe`, `store_sql`, `close_sql`, `close`, `get_autocommit`.
- SQL errors are `{"type":"error","error":{"message","code"}}` results inside a 200 response; `code` is always set.
- Batons are random 128-bit strings, rotated on every response, bound to the API key and database.
- Wire traps: `last_insert_rowid` always present (`null` or a decimal string); integers as strings; blob values are refused
  (an argument fails with `SQLITE_ERROR` "blob values are not supported"; results never hold blobs); no `Content-Type` requirement on requests; relaxed JSON escaping in responses.

### Scopes and limits

Reads need `documents:read`; any write or DDL needs `documents:write`, checked per statement after parsing. Every route
is under the existing QoS request budget (429 `tenant_busy`), like the existing SQL route. Bodies are at most 1 MiB,
SQL text 16 KiB per statement, 256 statements per request (the transaction log limit, SQ1-e7).

## 7. Slices

| PR | Content |
| --- | --- |
| SQ1-a | this document |
| SQ1-b | `Features/Sql`: parser/validator, table catalog, `CREATE/DROP TABLE`, `…/sql/execute` with DDL |
| SQ1-c | engine reads (`ExecuteSqlAsync`), `SELECT` with joins and parameters, limits |
| SQ1-d | `INSERT/UPDATE/DELETE` as lattice operations, keys, affinity, constraints, replication test |
| SQ1-e | transactions and sessions (in-request and replayed) |
| SQ1-f | Hrana v2/v3 pipeline |
| SQ1-g | Hrana v3 cursor; client proof scripts (`scripts/terrabase/libsql/`) |

`StorageFormat` is unchanged by every slice: the definition is a member of an existing metadata document.
