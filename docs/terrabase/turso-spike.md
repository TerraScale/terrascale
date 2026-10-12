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
  `dotnet run -c Release -- features | cdc | designc | bench <seconds> [engine-prefix|all] [upsert|designc|both] [1|2|both] | stress <s> [mvcc|wal] [writers] | lattice | tc | wrap | uring`.
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
   file, splits command text on every `;` (which breaks triggers), and decoded a 4097-row CDC page in 1100 ms
   against 38 ms for `TursoConnection` (Fly). `TursoConnection` executes only the **first** statement of a
   multi-statement command, so send one statement per command.
5. **No cancellation exists.** `Cancel()`, `CommandTimeout` and cancellation tokens are ignored, and the C API
   has no interrupt or progress handler. Bound every query before it runs.

**MVCC + CDC works in 0.8.2**, contrary to the plan's premise. On a database already in `mvcc` journal mode,
`capture_data_changes_conn('full')` captures `BEGIN CONCURRENT` commits. Turso only refuses to *switch* journal
mode while capture is on. Design C (`docs/terrabase/multi-region-replication.md`) still stays on WAL:
`BEGIN CONCURRENT` breaks the commit order that the changes feed and GC rely on (CDC groups interleave in
`change_id` order), and CDC takes away most of MVCC's speed anyway (see Throughput).

> **Correction (2026-10-12).** The first version of this report said CDC costs about 2% on the
> recommended open path. That was wrong: the probe enabled CDC only when the engine name *ended* in `cdc`, so the
> `-cdc-uring` and `-cdc-aegis256` runs never captured anything. The rerun below verifies capture on every run
> (`turso_cdc` rows per write). **CDC costs 37% of single-writer WAL throughput in mode `full` and 32% in mode
> `after`, on every open path.**

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
| Overhead | see Throughput | WAL, 1 writer, Fly: **−37% (`full`) / −32% (`after`)** for one captured row per transaction; −39% / −30% for design C's two rows. Identical on every open path. MVCC: −69% or more. `full` writes before + after images (6044 B vs 2015 B per 2 KB update). |
| MVCC + CDC | **GO, caveats** | 2 threads × 2000 `BEGIN CONCURRENT` inserts: 4000 rows, 4000 insert records, no duplicate `change_id`. But 1520 transaction groups **interleave** in `change_id` order, and rolled-back conflicts leave id gaps (max id 9577 for 8002 rows). A `change_id > cursor` poller can skip rows that commit later with lower ids. The shared `turso_cdc` table also turns into a conflict hotspot (3213 retries for 4000 transactions). |

### Design C dependencies (`docs/terrabase/multi-region-replication.md`)

Probe `designc`, run locally and on Fly (identical results), through both `TursoConnection` and the facade.
Tables are the design's own: `"<collection>"(id TEXT UNIQUE, doc TEXT)` and `_tb_state`.

| Behaviour | Verdict | Evidence | Fallback |
| --- | --- | --- | --- |
| CDC mode `after` | **GO** | `PRAGMA capture_data_changes_conn('after')`: insert and update rows carry `after` only (`before` and `updates` NULL), each transaction ends with a COMMIT row. A **delete row carries only the rowid** (`after` NULL), so a deleted document's key comes from its paired `_tb_state` update (`live = 0`), as the design already does. | `full` (costs 5–9% more throughput and 3× the CDC bytes). |
| Toggle capture per transaction | **GO** | `('off')` before `BEGIN IMMEDIATE` and `('after')` after `COMMIT`: nothing from that transaction is captured, not even a COMMIT row. Toggling **inside** an open `BEGIN IMMEDIATE` also works: only statements after `('after')` are captured. `DELETE FROM turso_cdc` with capture off keeps ids continuous. An off/on pair costs 0.12–0.23 ms, and works while a reader holds a read transaction. | Leave capture on and drop the tailer's own rows by table name. |
| `bin_record_json_object` on real rows | **GO, with a query guard** | Exact round trip: a 300 KB `doc` with emoji, CJK, quotes, backslashes, `\n` and `\u0001`; a 4000-char base64url `state`; `stamp`, `h1`, `h2` at `long.MinValue`/`long.MaxValue`; REAL and NULL. **Two failures:** BLOB columns throw (`formatting of BLOB values … is not supported`), and decoding a page that includes COMMIT rows throws, because `table_columns_json_array(NULL)` is an error. Guarded, a 4097-row page decodes in 12 ms locally and 38 ms on Fly (`TursoConnection`). | Decode with `CASE WHEN change_type IN (0, 1) THEN bin_record_json_object(table_columns_json_array(table_name), after) END`. Keep captured tables free of BLOBs (the design already does). |
| Planner uses expression indexes on per-collection tables | **GO, with rewrites** | 20 k docs, no partial index. Index used for `=` (literal and parameter), ranges, `ORDER BY expr LIMIT`, composite `(org, age)` with `org =` and `age >`, `count(*)`, and an extra unindexed filter. Same plans before and after `ANALYZE`. **Full scans** (10–16 ms vs 0.02 ms) for `IN (…)`, `IN (SELECT … json_each)`, `LIKE 'x%'`, the `doc ->> '$.f'` spelling, and the second column of a composite index alone. | The `SqlSubset` compiler emits `json_extract(doc, '$.f')` only, rewrites `IN` lists to `OR` (a `MULTI-INDEX OR` plan) or `UNION ALL`, and rewrites prefix `LIKE` to `>= p AND < p+1`. |

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

Two workloads, one write per transaction, 50% new ids and 50% updates, `synchronous=NORMAL`, 1 writer (design C's
write lane):

- **upsert**: one row in `documents(collection, id, state BLOB(256), version, body TEXT(222))` via
  `ON CONFLICT DO UPDATE`, with a partial live index.
- **designc**: the collection row (`id`, `doc` 222 B) plus the `_tb_state` row (base64url state 342 chars, ints),
  plain `UPDATE` or `INSERT`, so 2 captured rows per transaction.

### CDC cost, verified (Fly gru, shared-cpu-2x, 2026-10-12)

Each `-cdc` run checks `turso_cdc` afterwards: 0.98–1.00 captured rows per upsert, exactly 2.00 per design-C
write. Two passes, 8 s per engine; Microsoft.Data.Sqlite at the start and end of a pass showed no throttling
(1397 → 1418/s; designc 1267 → 1160/s).

| Open path | upsert, no CDC | `full` | `after` | designc, no CDC | `full` | `after` |
| --- | --- | --- | --- | --- | --- | --- |
| facade | 1228–1428 | 772–773 | 822–823 | 1028 | 629 | 708–722 |
| `TursoConnection` | 1209–1211 | 772–773 | 825–826 | 1011–1040 | 629–632 | 699–721 |
| native pre-open, `io_uring` | 1210–1214 | 767–768 | 821 | 1025–1027 | 628–629 | 711–716 |
| `TursoConnection` + aegis256 | 1206–1209 | 769–771 | 823–825 | 1025–1034 | 622–629 | 705–728 |
| Microsoft.Data.Sqlite WAL (reference) | 1397–1418 | | | 1160–1267 | | |
| Turso MVCC `BEGIN CONCURRENT` (reference) | 5018 | 858 (`full`) | | 3592 | 711 (`full`) | |

- **CDC `full` costs 37% (upsert) and 39% (designc); `after` costs 32% and 30%.** Open path, io_uring and
  encryption change nothing.
- **Encryption is free:** aegis256 matches unencrypted within 1% with or without CDC.
- Tail latency is unchanged by CDC: p99.9 is 230–265 ms in every WAL row (checkpoint stalls, see below).
- Local (load 14–25, pass 1 only; the second local pass was too noisy to use): upsert 11.4 k → 8.5 k (`full`,
  −25%) / 8.9 k (`after`, −22%); designc 9.4 k → 6.7 k (`full`, −29%).

### Engine comparison (Fly, 2026-10-11, upsert)

Burst CPU, 4 passes of 8–10 s. In those runs, rows labelled "+ CDC" with `io_uring` or aegis256 did **not**
capture (the bug in the correction above), so they appear here under their real configuration.

| Engine | 1 writer | 2 writers |
| --- | --- | --- |
| Microsoft.Data.Sqlite WAL | 1369–1516 | 1449–1580 |
| Turso WAL (facade) | 1202–1231 | 1572–1605 (crash-prone) |
| Turso WAL + CDC `full` (facade) | 765–772 | 880–928 |
| Turso WAL, native pre-open `io_uring`, no CDC | 1193–1207 | 1134–1190 (panicked in 2 of 4) |
| Turso WAL, aegis256 `TursoConnection`, no CDC | 1194–1206 | 1143–1382 |
| Turso MVCC `BEGIN CONCURRENT` | 5147–5626 | 5625–5847 |
| Turso MVCC + CDC `full` | 1577–1889 | 728–908 (14–26 k conflict retries) |

Tail latency (Fly, 1 writer):

| Engine | p50 ms | p99 ms | p99.9 ms | max ms |
| --- | --- | --- | --- | --- |
| Microsoft.Data.Sqlite WAL | 0.023 | 0.081 | 293.6 | 301 |
| Turso WAL + CDC `full` (`TursoConnection` + aegis256, 2026-10-12) | 0.209 | 0.566 | 246.5 | 251 |
| Turso MVCC | 0.127 | 0.233 | 0.5 | 691 |
| Turso MVCC + CDC | 0.540 | 1.243 | 2.0 | 106 |

What the numbers say:

- **WAL is bound by checkpoints on Fly volumes, for both engines.** About 1 commit in 1000 stalls 230–300 ms.
  `strace` shows neither engine fsyncs per commit under `synchronous=NORMAL` (41 fsyncs in 6 s for Turso).
- **CDC costs about a third of write throughput** (see the verified table). For design C on one shared-cpu-2x
  machine that is about 630–720 writes/s per database file at burst, before any merge work in C#.
- **MVCC is 4.5× faster than WAL** and has no checkpoint stalls, but CDC takes away most of that (5018 → 858),
  and with 2 writers the shared `turso_cdc` table conflicts constantly.
- **The WAL grows unbounded under 2 Turso writers:** 140–212 MB after 10 s (checkpoint starvation), against
  4–13 MB with 1 writer. On a 1 GB volume that is a disk-full risk.
- **Peak RSS** (5 s, 1 file): Microsoft.Data.Sqlite 57 MB, Turso WAL aegis256 76 MB, Turso MVCC+CDC
  **209 MB** (MVCC keeps versions in memory). The guest has 459 MB and TerraBase uses about 130 MB.
- **Shared-CPU throttling is real:** identical 30 s stress runs fell from 13.4 k to 2.0 k writes once the burst
  balance ran out. Size capacity on the baseline, not these burst numbers.

## Fast suite on the swap branch

Branch `feat/turso-engine-swap` (draft PR #21), real TerraBase code, `make terrabase-test-fast`. The run is
not comparable on speed: baseline at 16-way parallelism, Turso at 8, on a loaded box.

| Step | Fast suite | What blocked |
| --- | --- | --- |
| Baseline, Microsoft.Data.Sqlite | **1903 / 1903** in 2m07s | |
| Package swap + `TursoShim` (compiles) | almost all failed | Every database bootstrap: `WITHOUT ROWID` |
| Drop `WITHOUT ROWID`, trigger via `TursoConnection` | most failed | `PRAGMA query_only = ON` rejected (only `1`/`0`); `GetFieldValue<byte[]>` on TEXT throws (Microsoft.Data.Sqlite returns UTF-8 bytes); removing an unregistered UDF throws `Not Found` |
| Shim those three | **1654 / 1903** in 13m40s | The 249 below |

Remaining failure classes in the last run, by cause:

1. **Backups and branches:** `VACUUM INTO requires a string literal path` (794 server errors). Turso will not
   bind `VACUUM INTO $path`; it needs a literal.
2. **More than one writer connection:** `database is locked` (335 server errors). TerraBase opens several
   connections that write (sessions, maintenance). Turso needs one writer per file anyway (rule 1).
3. **Test fixtures:** they create generated columns (behind the `generated_columns` flag) or multi-statement
   triggers through the facade (`incomplete input`).
4. **Cancellation-dependent tests time out**, for example `A_paused_baseline_is_isolated…` and shutdown or
   stall tests. Interrupt and the QoS progress handler are no-ops.
5. **Singles:** `Expression tree is too large (maximum depth N)` on one generated query (Turso's depth limit is
   lower than SQLite's), plus downstream 404/500 assertions in backup listing tests.

None of these needs Turso changes except item 4. Items 1–3 are design changes 3c makes anyway.

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
   - enable `after` capture on the writer connection right after open, retrying until it gets the lock;
   - `GET /databases/{id}/changes` (SSE) and delta anti-entropy tail `turso_cdc` by `change_id` on a reader
     connection; with one WAL writer, id order is commit order;
   - decode after-images with `bin_record_json_object` behind a `CASE WHEN change_type IN (0, 1)` guard (COMMIT
     rows make `table_columns_json_array(NULL)` throw); never capture BLOB columns;
   - turn capture off for the tailer's own transaction (`('off')` … `('after')`, works inside the transaction);
   - truncate on the writer actor: `DELETE FROM turso_cdc WHERE change_id < min(consumer watermarks)`, always
     keeping the newest row;
   - persist consumer watermarks in the same file.
6. **SQL read-write:** parse and allow-list the user's DML against the document tables, then run it on the
   writer actor inside a transaction. Convert its own CDC rows (`change_id > mark`) into per-field LWW stamps
   (HLC + node ID) before commit.
7. **No cancellation:** reject or rewrite queries without a bounded `LIMIT`, keep the existing `EXPLAIN` plan
   checks, and give reads a bounded worker pool, because a running statement cannot be stopped. File an
   upstream issue for `turso_connection_interrupt`.
8. **Memory:** set `PRAGMA cache_size` per connection (there is no global heap limit). Keep WAL: MVCC + CDC
   works, but `BEGIN CONCURRENT` breaks the commit order the feed and GC rely on.
9. **Query compiler:** emit `json_extract(doc, '$.f')` only, rewrite `IN` lists to `OR` and prefix `LIKE` to
   ranges, so expression indexes are used.
10. **Out of launch scope:** FTS (needs a custom native build with `fts`), a dense vector index, Postgres wire.
   Brute-force vector functions and materialized views can ship.
11. **Crash safety net:** Fly restarts the machine on exit. Add a Slow test that drives the real write path
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
6. **CDC costs a third of write throughput** (−37% `full`, −32% `after`), measured with capture verified.
7. **Shared-CPU baseline.** Sustained throughput is a fraction of these burst numbers.

## Found while swapping the engine (T1, 2026-10-12)

TerraBase now runs on `Turso.Data.Sqlite.Provider` 0.8.2 (storage format 3). Behaviour the swap had to work around, each
reproduced with the provider alone; the workaround is in the code next to it:

| Behaviour | Workaround |
| --- | --- |
| `wal_autocheckpoint = 0` is ignored: Turso restarts its log by itself (11 times in 3000 commits) | Backup ships the change capture, not the log (`backup.md`) |
| The last connection's close does not checkpoint: the `-wal` file stays | `wal_checkpoint(TRUNCATE)` before a file is moved (create, restore) or copied, when the writer closes, and an empty `-wal` is deleted at shutdown |
| `Mode=ReadWrite` is ignored: opening a missing file creates it | `SqliteSession.OpenExisting` refuses a missing file and removes an empty one it created |
| `VACUUM INTO` on any connection but the writer copies an older view; it renumbers `sqlite_schema` rowids; `BackupDatabase` fails with "database is locked" | Copies are row by row in one read transaction (`TursoCopy`) |
| `wal_checkpoint(TRUNCATE)` waits for every reader on older frames; a read transaction stops Turso from checkpointing into the file (the log grows instead) | Neither is relied on for snapshots |
| A runtime error while a SELECT steps (a function that throws) rolls the whole transaction back; later statements then run in autocommit. Constraint, parse and `RAISE(FAIL)` errors do not | A failed statement in a write transaction checks `BEGIN` (`SqliteSession.InTransaction`); if the transaction is gone it can only be disposed |
| A write on a connection while one of its own SELECT readers is open fails with "database is locked" | Read the ids first, then write (`CaptureSystemDeletions`) |
| A failed statement without result columns (a write, `BEGIN`, `ROLLBACK`, a savepoint) is never finalized by the provider (Turso.Data.Sqlite 0.8.2 `SqliteCommand.Execute`): the garbage collector's finalizer thread finalizes it later, a second user of the connection that Turso refuses with "concurrent use forbidden" when the owner steps another statement then | `SqliteSession.ReleaseAbandonedStatements`: after a failed statement, a blocking collection finalizes it on the connection's own thread before the connection runs anything else (the runners, and the catches that keep using the connection). The `InTransaction` probe runs only after a failed statement. Slow `AbandonedStatementTests` reproduces it. Cost, measured on 2 CPUs and 512 MiB (benchmark/bench/2026-10-12-statement-drain.md): none without failing statements; about 2 forced gen2 collections (7 to 9 ms of pause each) per failing statement, process-wide, so 10 % failing writes cost 27 % of write throughput and double read p95 |
| Closing a reader steps the statement to its end | Every early-closed statement has a `LIMIT`, and the per-row `tb_qos_scan` stops a cancelled one |
| No interrupt, progress handler or VM instruction count | `tb_qos_scan(octet_length(doc))` in every scan: it accounts candidates, checks the deadline every 32 rows and cancellation every row, and throws to stop the statement. It also charges the plan's work budget per candidate row (8, plus 4 per JSON read), in place of SQLite's instruction count |
| A function without arguments is evaluated once per statement | `tb_query_scan(id)` takes the row's id |
| A subquery in FROM is not flattened (it is materialized, then sorted) | Document queries and the history reads are single flat statements |
| A row-value comparison `(a, b) > (?, ?)`, or the same as an `OR`, scans the whole index; `max(x)` scans | Two bounded seeks joined by `UNION ALL`; `ORDER BY x DESC LIMIT 1` |
| An expression deeper than 100 is refused (SQLite allows 1000) | `AND`/`OR` chains are emitted as balanced trees |
| Planner statistics are read only when a connection opens | An index build closes older connections when they are returned |
| The planner ignores `LIMIT`: an unfiltered `ORDER BY` on an indexed, non-covered field scans and sorts | The SQL subset names the index (`INDEXED BY`) for an unfiltered sort on its leading field |
| The stored index SQL has a space after a function name (`json_extract (doc, …)`) | The index parser accepts it |
| EXPLAIN says `USE SORTER FOR ORDER BY` for SQLite's `USE TEMP B-TREE`, and `MULTI-INDEX OR` for an `IN` rewritten to `OR` | The sort budget and the used-index report read Turso's wording |
| Change capture records the writer's TEMP tables as if they were in the file, records a DDL statement as a `sqlite_schema` row (a drop as its rowid only), and does not record `ANALYZE`'s `sqlite_stat1` | No TEMP tables on the writer (`_tb_unique_affected`); the backup log names dropped objects |
| The provider splits command text at every `;` and cannot wrap a local `TursoConnection`, so it cannot create a trigger; it has no batches for local files | No triggers in the schema; tests that inject failures create triggers on a connection of their own while the writer is held |

