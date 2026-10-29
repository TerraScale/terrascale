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
and describe one statement (prepare without stepping: parameter names, result columns and declared types). Both use
Turso's `prepare_first`, so statement boundaries come from the engine's own parser: a single statement call refuses any
text after its first statement, and a script runs one statement at a time from the engine-reported tail, so later
statements see earlier DDL. Failures carry a stable kind (constraint kinds, syntax, no such table or column,
busy, too big, interrupted, read-only) with messages that may name a table or column but never values or statement
text.

## Sessions and transactions

- Autocommit: each statement runs in its own storage transaction, a write transaction when it writes, and commits.
- `BEGIN [DEFERRED|IMMEDIATE|EXCLUSIVE] [TRANSACTION]`, `START TRANSACTION`, `BEGIN READ ONLY`, `COMMIT`/`END` and
  `ROLLBACK` control one storage transaction pinned to the session across requests. It opens at the first statement
  after `BEGIN`: a write transaction unless the key cannot write or the transaction is read-only.
- A pinned transaction lives at most 5 seconds (`SqlLimits.MaxTransactionTime`). A timer then interrupts any running
  statement, rolls the transaction back and releases the writer even when the client is idle; the next statement, and
  `COMMIT`, answer `SQLITE_INTERRUPT`, and `ROLLBACK` ends it.
- Every statement runs with a native query timeout of min(5 s, the request's remaining time, the transaction's
  remaining life), and a cancelled request interrupts it (`turso_connection_interrupt`, sqlite3_interrupt semantics).
  An interrupted statement answers `SQLITE_INTERRUPT`. Turso clears the interrupt once the connection is idle, so a
  pooled connection runs its next statement normally.
- A write transaction takes the organization's storage-quota lease before the database writer (the same order as
  every other writer), holds it until commit, rollback or expiry, and never waits for the lease while holding the
  writer.
- Inside a transaction every write runs under a savepoint: a failed statement undoes only itself. `SAVEPOINT`,
  `RELEASE` and `ROLLBACK TO` pass through inside a transaction, also over the PostgreSQL wire, where `ROLLBACK TO`
  clears the aborted state. If the engine itself ends the transaction after a failure, the session answers
  `SQLITE_ABORT` until `ROLLBACK`.
- A transaction's statements change at most 1024 rows and grow the file by at most 8 MiB in total, rolled-back
  work included (`SQLITE_TOOBIG`). A result holds at most 10,000 rows and 8 MiB: value sizes are read natively before a
  value is copied, and the connection's native value-length bound (`turso_connection_set_max_value_length`) refuses
  `zeroblob`, `randomblob`, `||` and function results past 8 MiB before or as they are built.
- A deferred foreign key (`DEFERRABLE INITIALLY DEFERRED`) violated at `COMMIT` is a definite rejection: the
  transaction rolls back without faulting the copy, and the commit answers `SQLITE_CONSTRAINT_FOREIGNKEY` (23503).

## Guard

Until a real authorizer exists, every name in a statement (bare and quoted identifiers and string literals, anywhere,
trigger and view bodies included) is checked by a tokenizer that respects SQLite quoting and comments
(`src/TerraBase/Engine/SqlText.cs`, `src/TerraBase/Features/Sql/SqlScript.cs`). Refused: names starting with `_tb_`,
`turso_`, `__turso_`, `tb_index_`, `fts_dir_`, `pragma_` or `sqlite_stat`; the engine tables `collections`,
`commit_offset`, `_collections`, `_indexes` and `_unique`; and, in a write, any name equal to one of the database's
document collections. A literal value equal to a reserved name is refused too: a rare false positive kept for safety.
Also refused: `ATTACH`, `DETACH`, `VACUUM`, `ANALYZE`, temporary and virtual tables, and every `PRAGMA` except
`table_info`, `table_xinfo`, `table_list`, `index_list`, `index_info`, `index_xinfo`, `foreign_key_list`,
`foreign_key_check` and `foreign_keys` (read, or set to on; off is refused). `sqlite_schema` is readable.

## Values, errors and metering

- Values are null, 64-bit integers, doubles, text and blobs. Over HTTP a blob is `{"base64":"…"}` in arguments and
  rows; over the PostgreSQL wire it is `bytea`.
- Errors keep SQLite codes: `SQLITE_CONSTRAINT_PRIMARYKEY`, `_UNIQUE`, `_NOTNULL`, `_CHECK`, `_FOREIGNKEY`,
  `SQLITE_BUSY`, `SQLITE_TOOBIG`, `SQLITE_INTERRUPT`, `SQLITE_AUTH`, `SQLITE_READONLY`, `SQLITE_FULL` and
  `SQLITE_ERROR`. The PostgreSQL wire maps them to `23505`, `23502`, `23514`, `23503`, `40001`, `54000`, `57014` and
  `42501`.
- Reads of tables are charged per returned row by bytes, once the protocol accepts the rows. Writes are charged after
  commit for surviving rows only: `ROLLBACK TO` restores the billing state of its savepoint (client savepoints and
  the per-statement ones). One write unit and one operation per row a statement inserted, updated or deleted; rows a
  trigger or a cascade changes are not billed separately.
- SQL storage counts against the organization's storage quota. A write transaction measures the file's used bytes
  ((page_count − freelist_count) × page_size) when it opens and at commit; the net change is checked against the quota
  (`SQLITE_FULL` past it) and kept in the database's `_tb_collection_stats` row `_sql`, committed with the
  transaction, so it survives restarts, never counts rolled-back work, and shrinks when deletes free pages. SQL commits
  advance the database's commit sequence, which keeps the accounting cache current.

## Native exports

`src/vendor/Turso.Native/patches/0004-export-interrupt-query-timeout-and-value-length.patch` adds
`turso_connection_interrupt`, `turso_connection_set_query_timeout_ms` and `turso_connection_set_max_value_length` to
the pinned Turso 0.8.2 build; the vendored driver binds them (patch 0013). See `src/vendor/Turso.Native/README.md`
for the release that carries them.
