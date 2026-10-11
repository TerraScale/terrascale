# Turso spike, phase 1b (2026-10-11)

What actually works when TerraBase runs on Turso through `Turso.Data.Sqlite.Provider`, measured before phase 3c
commits to it. Decisions live in `.cos/fly-migration-plan.md`; this file records what the engine did.

- **Versions**: `Turso.Data.Sqlite.Provider` 0.8.2 (latest on nuget.org; native `libturso_sdk_kit`, 72 MB,
  `sqlite_version()` = 3.50.4), Microsoft.Data.Sqlite 11.0.0-rc.1 for comparison, .NET 11 rc.1. Turso source
  read at `tursodatabase/turso@120511cb` (main, 2026-10-11).
- **Local**: 16-core dev box with load average 60–90 from other work, so local numbers only show function,
  not speed.
- **Fly**: throwaway app `spike-turso`, gru, shared-cpu-2x, 512 MB + 4 GB swap, 1 GB volume, kernel
  `6.12.105-fly`. Destroyed afterwards.
- **Repro**: `benchmark/dotnet/TursoSpike` (standalone, outside the solution):
  `dotnet run -c Release -- features | cdc | bench <seconds> [engine-prefix] | stress <s> [mvcc|wal] [writers] | lattice | tc | wrap | uring`.
- **Engine swap branch**: `feat/turso-engine-swap` (draft PR), the real TerraBase on Turso.

## Verdict

Turso is usable for 3c, but only on a narrow path. Every item below is a hard rule, not a preference:

1. **One writer connection per database file.** Two connections writing the same file in WAL mode abort the
   whole process with a Rust panic (three different sites, 5 of 13 two-writer runs).
2. **Rowid tables only.** `WITHOUT ROWID` is behind a flag, and even with the flag UPDATE, DELETE, `ON CONFLICT`
   and secondary indexes fail on it. CDC skips those tables too.
3. **Open each database natively first.** A ~40-line P/Invoke of `turso_database_new` sets experimental
   features (`views`, `index_method`, `attach`), `io_uring` and the cipher. Connections opened afterwards
   share that `Database` through Turso's process-wide registry. Neither .NET API exposes these settings.
4. **Use `Turso.TursoConnection`, not the `Turso.Data.Sqlite` facade.** The facade cannot open an encrypted
   file, splits command text on every `;` (which breaks triggers), and its default open path made WAL+CDC 37%
   slower. `TursoConnection` executes only the **first** statement of a multi-statement command, so send one
   statement per command.
5. **No cancellation exists.** `Cancel()`, `CommandTimeout` and cancellation tokens are ignored, and the C API
   has no interrupt or progress handler. Bound every query before it runs.

**MVCC + CDC works in 0.8.2**, contrary to the plan's premise. On a database already in `mvcc` journal mode,
`capture_data_changes_conn('full')` captures `BEGIN CONCURRENT` commits. Turso only refuses to *switch* journal
mode while capture is on. It is still not the launch choice (see Throughput and Risks).

## GO / NO-GO

### Engine compatibility (TerraBase's current SQL)

| Feature | Verdict | Evidence / workaround |
| --- | --- | --- |
| Drop-in compile | **GO** | TerraBase builds after `using Turso.Data.Sqlite` plus removing 6 raw SQLitePCL call sites (`TursoShim.cs` on the swap branch). |
| Raw `sqlite3*` handle | **NO-GO** | `SqliteConnection.Handle` returns `null` by design. |
| `sqlite3_interrupt`, progress handler, `Cancel()`, `CommandTimeout`, async tokens | **NO-GO** | A 30 M-row recursive CTE ran to completion (55–59 s) under all four. No C export exists. Repro: `features`. |
| `soft_heap_limit` | **NO-GO** | No API. Bound memory with per-connection `PRAGMA cache_size`. |
| `WITHOUT ROWID` | **NO-GO** | Without the flag: "experimental feature". With `without_rowid`: "UPDATE of WITHOUT ROWID tables is not supported", same for DELETE and CREATE INDEX, and `ON CONFLICT` "does not match any PRIMARY KEY". |
| Triggers | **WORKAROUND** | The engine runs them. The facade's statement splitter cuts `BEGIN … ; … END` ("incomplete input"); through `TursoConnection` they work. |
| Partial indexes, `STRICT`, `RETURNING`, upsert `ON CONFLICT … DO UPDATE … WHERE` | **GO** | |
| `json_extract` expression index | **WORKAROUND** | Created and usable, but the planner picked the overlapping partial index `(collection, id) WHERE body IS NOT NULL` instead. `INDEXED BY` gives `SEARCH d USING INDEX d_email (collection=? AND json_extract(body,'$.email')=?)`. |
| `json_each`, `json_tree`, `jsonb*`, `json_patch`, `json_set` | **GO** | |
| Savepoints (SQL and ADO `Save`/`Rollback(name)`) | **GO** | The Turso manual still says "no savepoints"; 0.8.2 has them. |
| PRAGMAs TerraBase sets | **GO** | `journal_mode=WAL`, `synchronous=NORMAL`, `user_version`, `query_only`, `temp_store`, `cache_size`, `mmap_size`, `wal_autocheckpoint`, `wal_checkpoint(TRUNCATE/PASSIVE)`, `busy_timeout`, `schema_version`, `integrity_check`, `optimize` are all accepted. `locking_mode` reports `exclusive`. |
| Busy handling | **WORKAROUND** | `busy_timeout` is honored (second `BEGIN IMMEDIATE` failed after 200 ms). Every local error has code **1**, so match `"database is locked"` by message. `PRAGMA capture_data_changes_conn` takes the write lock and does **not** wait on `busy_timeout`: retry it. |
| Concurrent writer connections (WAL) | **NO-GO** | Process abort: `wal.rs:3590 end_write_tx called while write lock not held`, `pager.rs:269`, `btree.rs:8840 self.current_page >= 0`. Hit in 5 of 13 two-writer runs (Fly and local, with and without io_uring). A Rust panic kills the .NET process; nothing can catch it. |
| Scalar UDFs (`CreateFunction`) | **GO** (facade only) | `TursoConnection` has no UDF API. |
| `ANALYZE` / `sqlite_stat1`, `VACUUM INTO`, `BackupDatabase`, `TEMP` tables | **GO** | |
| `ATTACH` | **WORKAROUND** | Needs the `attach` feature (native open). |
| Multi-statement command text | **WORKAROUND** | Facade: runs all statements, but its naive splitter breaks on triggers. `TursoConnection`: **runs only the first statement and reports success**; a later statement that refers to an earlier one fails to prepare. Use one statement per command. |

### CDC (`PRAGMA capture_data_changes_conn('full')`)

| Question | Verdict | Evidence |
| --- | --- | --- |
| Capture + decode | **GO** | `bin_record_json_object(table_columns_json_array('documents'), after)` gives `{"collection":"c","id":"a","version":1,"body":"{\"x\":1}"}`. Every transaction ends with a `change_type = 2` COMMIT row; `change_txn_id` groups its rows. |
| Scope | **GO** | Per connection: a connection without the pragma is not captured. Rolled-back transactions leave no rows. |
| Before-image of `ON CONFLICT DO UPDATE` | **NO-GO (bug)** | `before` holds the **new** row, and `updates` is NULL. Plain `UPDATE` is correct (before = old row; `updates` = per-column flags such as `{"collection":0,"id":0,"version":1,"body":1}`). `INSERT OR REPLACE` is captured as DELETE (correct before) + INSERT. |
| Poll while writers run | **GO** | A reader tailing `change_id > last` saw 401/401 writes from 2 writer threads with no gaps. On Fly, 1 writer + 2 point readers + 1 poller ran 30 s × 3 with no crash. |
| Truncating consumed rows | **WORKAROUND** | `DELETE FROM turso_cdc WHERE change_id <= ?` works from any connection and ids continue. `turso_cdc` is declared `AUTOINCREMENT`, but once it is **empty** a reopen restarts `change_id` at 1. Always keep the newest row. |
| Schema drift | caveat | `table_columns_json_array()` returns the *current* columns, so old rows decode wrongly after `ALTER TABLE`. |
| Overhead | see Throughput | WAL: 0–37% depending on the open path. MVCC: −69%. Full mode writes each updated row three times (row, before, after). |
| MVCC + CDC | **GO, caveats** | 2 threads × 2000 `BEGIN CONCURRENT` inserts: 4000 rows, 4000 insert records, no duplicate `change_id`. But 1520 transaction groups **interleave** in `change_id` order, and rolled-back conflicts leave id gaps (max id 9577 for 8002 rows). A `change_id > cursor` poller can skip rows that commit later with lower ids. The shared `turso_cdc` table also turns into a conflict hotspot (3213 retries for 4000 transactions). |

### Launch features

| Feature | Verdict | Evidence / workaround |
| --- | --- | --- |
| Encryption at rest + WAL + CDC | **GO** via `TursoConnection` | `Encryption Cipher=aegis256;Encryption Key=<64 hex>`: CDC decodes, and the marker string is absent from `.db` and `-wal`. A wrong key fails with `Decryption failed for page=1`; a keyless open fails with `file is not a database`. Overhead ≈ 0 (see Throughput). |
| Encryption through the facade | **NO-GO** | `Turso.Data.Sqlite` rejects `Encryption Cipher`, throws on `Password`, and cannot wrap an encrypted `TursoConnection` ("Keyword not supported"). A native pre-open with the cipher is refused by facade opens ("Database is encrypted but no encryption options provided"). |
| Encryption + experimental features | **GO** | Native open with `encryption,views,index_method` + cipher, then `TursoConnection` with the same key: a materialized view works and no plaintext reaches disk. Key handling: one hex key string per open. Plan: unwrap the per-database data key with the fly-secret TerraScale key and pass it as hex. |
| Vector search, brute force | **GO** | `vector32`, `vector_distance_cos` etc. are compiled in. |
| Vector index | **WORKAROUND** | Only `toy_vector_sparse_ivf` (sparse vectors, needs `index_method`) is in the native lib. There is no dense ANN index. |
| Full-text search | **NO-GO** | The shipped native lib is built without the `fts` (tantivy) feature: `unknown module name 'fts'`, `no such module: fts5`. Needs a custom native build. |
| Incremental materialized views (DBSP) | **GO** via native pre-open | Needs the `views` feature (the facade never enables it). `count`/`sum` stayed correct across insert, update and delete, and survived a new connection. |
| Postgres wire | **NO-GO** in process | It is a separate crate (`turso_pg_server`, `pgwire` 0.36) hosted only by the `tursopg` binary (`--server <addr>`). It is not in the .NET native lib, has no release asset, and Turso does not support two processes on one file. |
| io_uring | **GO** | Fly allows it (`io_uring_disabled = 0` on 6.12). Selected with `vfs = "io_uring"` in the native open. It did not prevent the two-writer panics. |

### Read-write SQL as lattice writes

**GO (design), verified once.** Turso exports no update, commit or authorizer hook; CDC is the only hook, and
it is enough. CDC rows are visible to the writing connection *before* commit. So TerraBase can:

1. run the user's statement inside its own writer transaction;
2. read `turso_cdc WHERE change_id > mark`;
3. take the changed fields from `updates` and the values from `after`;
4. write HLC stamps;
5. commit once.

Repro `lattice`: `UPDATE users SET name = upper(name), doc = json_set(doc,'$.y',3) WHERE age > 25` produced 1 CDC
row, with stamps `a.name` and `a.doc` committed in the same transaction. Limits:

- Upsert `DO UPDATE` loses the before-image and `updates`, so diff it against the stored lattice instead.
- JSON bodies need a field diff in C#.
- With no authorizer, user SQL must be parsed and allow-listed (no writes to `turso_cdc`, system tables or DDL).

## Throughput

TerraBase-style write: one upsert per transaction into `documents(collection, id, state BLOB(256), version,
body TEXT(222))` with a partial live index; 50% inserts, 50% updates of existing ids; `synchronous=NORMAL`.
Values are upserts/s per pass on Fly gru (shared-cpu-2x, **burst** CPU, 1 GB volume; 4 passes of 8–10 s).

| Engine | 1 writer | 2 writers |
| --- | --- | --- |
| Microsoft.Data.Sqlite WAL | 1369–1516 | 1449–1580 |
| Turso WAL (facade) | 1202–1231 | 1572–1605 (crash-prone) |
| Turso WAL + CDC (facade default open) | 765–772 | 880–928 |
| Turso WAL + CDC, native pre-open (`io_uring`) | 1193–1207 | 1134–1190 (panicked in 2 of 4) |
| Turso WAL + CDC + aegis256 (`TursoConnection`) | 1194–1206 | 1143–1382 |
| Turso MVCC `BEGIN CONCURRENT` | 5147–5626 | 5625–5847 |
| Turso MVCC + CDC | 1577–1889 | 728–908 (14–26 k conflict retries) |

Tail latency (Fly, 1 writer, last pass):

| Engine | p50 ms | p99 ms | p99.9 ms | max ms |
| --- | --- | --- | --- | --- |
| Microsoft.Data.Sqlite WAL | 0.023 | 0.081 | 293.6 | 301 |
| Turso WAL + CDC + aegis256 | 0.113 | 0.263 | 257.9 | 267 |
| Turso MVCC | 0.127 | 0.233 | 0.5 | 691 |
| Turso MVCC + CDC | 0.540 | 1.243 | 2.0 | 106 |

What the numbers say:

- **WAL is bound by checkpoints on Fly volumes, for both engines.** About 1 commit in 1000 stalls 260–300 ms.
  `strace` shows neither engine fsyncs per commit under `synchronous=NORMAL` (41 fsyncs in 6 s for Turso).
- **With the recommended open path, CDC costs about 2%** in WAL (1215 → 1193–1206). The facade's default path
  loses 37% (766); the cause was not isolated.
- **Encryption costs nothing measurable** (aegis256: 1194–1206 vs 1202–1231 unencrypted).
- **MVCC is 4.5× faster than WAL** and has no checkpoint stalls, but CDC takes away 69% of that. With 2 writers
  the shared `turso_cdc` table conflicts constantly.
- **The WAL grows unbounded under 2 Turso writers:** 140–212 MB after 10 s (checkpoint starvation), against
  4–13 MB with 1 writer. On a 1 GB volume that is a disk-full risk.
- **Peak RSS** (5 s, 1 file): Microsoft.Data.Sqlite 57 MB, Turso WAL+CDC+aegis256 76 MB, Turso MVCC+CDC
  **209 MB** (MVCC keeps versions in memory). The guest has 459 MB and TerraBase uses about 130 MB.
- **Shared-CPU throttling is real:** identical 30 s stress runs fell from 13.4 k to 2.0 k writes once the burst
  balance ran out. Size capacity on the baseline, not these burst numbers.
- Local runs (load 60–90) ranked the engines the same way, but their absolute values are noise.

## Fast suite on the swap branch

FAST_SUITE_RESULTS

## Recommended 3c implementation

1. **New `Engine/Turso/` implementation** of the existing engine interfaces, starting from the swap branch.
   Delete `Engine/Sqlite/` when it lands (greenfield, no migration).
2. **Open path.** A `TursoDatabaseHandle` per database file:
   - P/Invoke `turso_database_new` + `turso_database_open` with features `views,index_method` (plus
     `encryption`), `vfs = "io_uring"` on Linux, cipher `aegis256` and the hex data key;
   - hold the handle for the database's lifetime;
   - open every connection as `Turso.TursoConnection` with the same cipher and key (registry hit).

   File upstream PRs to accept `Experimental Features` / `Vfs` keywords and encryption in the facade, then drop
   the P/Invoke.
3. **One writer connection per database**, owned by the per-core actor that owns the database (this matches
   the Anna actor model), plus pooled reader connections. Nothing else ever writes, including CDC truncation
   and maintenance: they run as messages on the writer actor. Retry `"database is locked"` by message.
4. **Schema:** rowid tables only, no triggers, no `WITHOUT ROWID`, no upsert `DO UPDATE` where the before-image
   or `updates` matters (use `UPDATE`, then `INSERT` when 0 rows changed). Do not create overlapping partial
   indexes, or emit `INDEXED BY` from the query compiler. One statement per command.
5. **CDC:**
   - enable `full` capture on the writer connection right after open, retrying until it gets the lock;
   - `GET /databases/{id}/changes` (SSE) and delta anti-entropy tail `turso_cdc` by `change_id` on a reader
     connection; with one WAL writer, id order is commit order;
   - decode after-images with `bin_record_json_object` and a pinned column list (do not call
     `table_columns_json_array` after a schema change);
   - truncate on the writer actor: `DELETE FROM turso_cdc WHERE change_id < min(consumer watermarks)`, always
     keeping the newest row;
   - persist consumer watermarks in the same file.
6. **SQL read-write:** parse and allow-list the user's DML against the document tables, then run it on the
   writer actor inside a transaction. Convert its own CDC rows (`change_id > mark`) into per-field LWW stamps
   (HLC + node ID) before commit.
7. **No cancellation:** reject or rewrite queries without a bounded `LIMIT`, keep the existing `EXPLAIN` plan
   checks, and give reads a bounded worker pool, because a running statement cannot be stopped. File an
   upstream issue for `turso_connection_interrupt`.
8. **Memory:** set `PRAGMA cache_size` per connection (there is no global heap limit). Keep WAL; revisit MVCC
   only once Turso calls it production ready and CDC stops being a conflict hotspot.
9. **Out of launch scope:** FTS (needs a custom native build with `fts`), a dense vector index, Postgres wire.
   Brute-force vector functions and materialized views can ship.
10. **Crash safety net:** Fly restarts the machine on exit. Add a Slow test that drives the real write path
    with the single-writer rule and fails on process exit.

## Risks

1. **Process-abort panics.** Any Rust panic in `turso_statement_step` kills TerraBase. Three were found with
   only two writers; more may exist in paths not tested here. Mitigation: single writer per file, crash
   restart, upstream issues with the repro (`bench 6 turso-wal-cdc-uring`, 2 writers, crashed 1 of 1 locally
   with a backtrace through `pager.rs:3435` → `wal.rs:3590`).
2. **Maturity.** 0.8.x: CDC is "early preview", encryption and views are experimental, MVCC is "not production
   ready". Expect bugs like the upsert before-image and the `change_id` reset.
3. **Runaway queries** cannot be stopped and pin a worker until they finish.
4. **Checkpoint stalls** of 260–300 ms on Fly volumes at p99.9 (this affects SQLite too).
5. **The facade differs silently** from Microsoft.Data.Sqlite: error codes, splitting, encryption, speed. 3c
   should not rely on it.
6. **Shared-CPU baseline.** Sustained throughput is a fraction of these burst numbers.
