# Native SQL

- Status: implemented, 2026-10-29 (PIVOT task E1, PR 1). Replaces the SQL-to-document bridge.
- Surfaces: `POST /v1/projects/{project}/databases/{database}/sql/execute`, the PostgreSQL wire and Hrana all run on
  `ISqlService` (`src/TerraBase/Features/Sql/SqlService.cs`).

## Storage

A SQL table is a real table in the database's own Turso file, beside the engine's tables. `CREATE`, `ALTER` and
`DROP TABLE`, `CREATE [UNIQUE] INDEX` (partial and expression indexes), views and triggers pass through to the engine.
JSON columns are plain `TEXT`; `json_extract`, `->`, `->>` and `json_each` work on them. Every client connection runs
with `PRAGMA foreign_keys = ON`, so foreign keys, `ON DELETE CASCADE` included, are always enforced.

SQL tables are not document collections: they do not appear in the documents API, and they do not replicate to other
regions yet. Continuous backup captures their changes like any other table of the file.

## Engine boundary

Features never see Turso types. `IStorageTransaction` exposes two engine-neutral primitives
(`src/TerraBase/Engine/SqlStatement.cs`): run one statement (text, positional and named arguments, row and byte bounds)
and describe one statement (prepare without stepping: parameter names, result columns and declared types). Both refuse
more than one statement per call. Failures carry a stable kind (constraint kinds, syntax, no such table or column,
busy, too big, interrupted, read-only) with messages that may name a table or column but never values or statement
text.

## Sessions and transactions

- Autocommit: each statement runs in its own storage transaction, a write transaction when it writes, and commits.
- `BEGIN [DEFERRED|IMMEDIATE|EXCLUSIVE] [TRANSACTION]`, `START TRANSACTION`, `BEGIN READ ONLY`, `COMMIT`/`END` and
  `ROLLBACK` control one storage transaction pinned to the session across requests. It opens at the first statement
  after `BEGIN`: a write transaction unless the key cannot write or the transaction is read-only.
- A pinned transaction lives at most 5 seconds (`SqlLimits.MaxTransactionTime`). A timer then rolls it back and
  releases the writer even when the client is idle; the next statement, and `COMMIT`, answer `SQLITE_INTERRUPT`, and
  `ROLLBACK` ends it.
- Inside a transaction every write runs under a savepoint: a failed statement undoes only itself. `SAVEPOINT`,
  `RELEASE` and `ROLLBACK TO` pass through inside a transaction, also over the PostgreSQL wire, where `ROLLBACK TO`
  clears the aborted state. If the engine itself ends the transaction after a failure, the session answers
  `SQLITE_ABORT` until `ROLLBACK`.
- A transaction changes at most 1024 rows and grows the file by at most 8 MiB (`SQLITE_TOOBIG`); a result holds at
  most 10,000 rows and 8 MiB.

## Guard

Until a real authorizer exists, statements are classified by a tokenizer that respects SQLite quoting, comments and
trigger bodies (`src/TerraBase/Engine/SqlText.cs`, `src/TerraBase/Features/Sql/SqlScript.cs`). Refused: names starting
with `_tb_`, `turso_`, `__turso_`, `tb_index_`, `fts_dir_`, `pragma_` or `sqlite_stat`; the engine tables
`collections`, `commit_offset`, `_collections`, `_indexes` and `_unique`; writes naming a document collection's
table; `ATTACH`, `DETACH`, `VACUUM`, `ANALYZE`, temporary and virtual tables; and every `PRAGMA` except `table_info`,
`table_xinfo`, `table_list`, `index_list`, `index_info`, `index_xinfo`, `foreign_key_list`, `foreign_key_check` and
`foreign_keys` (read, or set to on; off is refused). `sqlite_schema` is readable.

## Values, errors and metering

- Values are null, 64-bit integers, doubles, text and blobs. Over HTTP a blob is `{"base64":"…"}` in arguments and
  rows; over the PostgreSQL wire it is `bytea`.
- Errors keep SQLite codes: `SQLITE_CONSTRAINT_PRIMARYKEY`, `_UNIQUE`, `_NOTNULL`, `_CHECK`, `_FOREIGNKEY`,
  `SQLITE_BUSY`, `SQLITE_TOOBIG`, `SQLITE_INTERRUPT`, `SQLITE_AUTH`, `SQLITE_READONLY`, `SQLITE_FULL` and
  `SQLITE_ERROR`. The PostgreSQL wire maps them to `23505`, `23502`, `23514`, `23503`, `40001`, `54000`, `57014` and
  `42501`.
- Reads of tables are charged per returned row by bytes, once the protocol accepts the rows. Writes are charged after
  commit: one write unit per changed row, and one operation per inserted, updated or deleted row. The storage quota
  sees the file's page growth, and refuses a commit past it with `SQLITE_FULL`.

## Known limits

- Turso 0.8.2 cannot interrupt a running statement: the row loop checks the deadline and work budget between rows,
  but a statement that computes without returning rows runs to its end.
- SQL table bytes are not yet part of the organization's measured storage; only each commit's growth is checked.
