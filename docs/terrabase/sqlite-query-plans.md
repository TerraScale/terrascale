# SQLite statements, query plans and connection settings

Scope: `src/TerraScale.TerraBase/Engine/Sqlite`. Plans are from `EXPLAIN QUERY PLAN` on the
SQLite that Microsoft.Data.Sqlite bundles (3.53.4). The Fast suite
(`Documents/QueryPlanTests.cs`) asserts the "after" plans for every statement in
`SqliteStatements.All`, so a statement that loses its index fails the build.

## Inventory

Only the engine runs SQL. Catalog, indexing and replication have no SQL yet (their slices are
interfaces only). API keys and the Management slice (organizations, projects, databases) are
documents in `_system` and use the document statements: lookups by id or unique name use `Get`, creates
and renames use `Put` and `Delete`, list endpoints and "has children" checks use `ListFirst` and `ListAfter`.
The plan test runs every statement against both a client database and the `_system` database
(plans depend on the schema, which is the same in both, not on the rows).

Schema v1: `documents (collection, id, version, body)`, `PRIMARY KEY (collection, id)`,
`WITHOUT ROWID`. The primary key is the clustered B-tree, so every primary-key lookup is
covering. No secondary index is needed today.

| Statement | Hot path | Plan before | Plan after |
| --- | --- | --- | --- |
| `Get`: `SELECT … WHERE collection = ? AND id = ?` | every document read, every API-key check | `SEARCH documents USING PRIMARY KEY (collection=? AND id=?)` | unchanged |
| `Put`: `INSERT … ON CONFLICT (collection, id) DO UPDATE … RETURNING version` | every document write | no plan rows (conflict check is a primary-key probe) | unchanged |
| `Delete`: `DELETE … WHERE collection = ? AND id = ?` | every document delete | `SEARCH documents USING PRIMARY KEY (collection=? AND id=?)` | unchanged |
| List, first page: `… WHERE collection = ? ORDER BY id LIMIT ?` | list without cursor | `SEARCH documents USING PRIMARY KEY (collection=?)` | unchanged (`ListFirst`) |
| List, next page: `… WHERE collection = ? AND id > ? ORDER BY id LIMIT ?` | list with cursor | `SEARCH documents USING PRIMARY KEY (collection=?)` — one statement used `($after IS NULL OR id > $after)`, which kept SQLite from seeking to the cursor: every page walked the collection from its first id | `SEARCH documents USING PRIMARY KEY (collection=? AND id>?)` (`ListAfter`) |
| `PRAGMA user_version`, `PRAGMA journal_mode = WAL`, `CREATE TABLE` | once per database per process (migration) | DDL, no plan | unchanged |
| `VACUUM INTO $path` | database copy (Management API), rare | maintenance, `EXPLAIN QUERY PLAN` returns no rows | unchanged; runs on a pooled connection |

No statement scans a table or sorts in a temporary B-tree. There is no query on JSON fields yet,
so there is no `json_extract` expression index; add one with the first statement that filters or
sorts on a document field, and add its plan here and to the test.

## Connection settings

Applied once per connection (`SqliteSession`), not per transaction.

| Setting | Value | Why |
| --- | --- | --- |
| `journal_mode` | `WAL` | Persistent in the file (set at migration). Readers never block the writer. |
| `synchronous` | `NORMAL` | Decision 0003 (`docs/decisions/0003-sqlite-synchronous-normal.md`). A commit no longer waits for an fsync; SQLite syncs the WAL before each checkpoint. Committed transactions, including acknowledged writes, can be lost after a power loss or an OS crash, but not after a process crash, and the file is never corrupted. Accepted for write throughput. Every connection uses it, including the one that creates a database. |
| `busy_timeout` | 30 s | SQLite's own busy handler (1–100 ms back-off) instead of Microsoft.Data.Sqlite's retry loop, which sleeps 150 ms between attempts: a writer behind a 20 ms write lock waited 150 ms before, 34 ms after. Same 30 s bound as the command timeout. |
| `mmap_size` | 256 MiB | Reads map pages instead of copying them through `read()`: about 5–10 % faster point reads and 25 % faster full listings in the benchmark below. Address space only; pages are the OS page cache. |
| `cache_size` | default (2 MiB) | With mmap, reads bypass most of the page cache. Per-connection memory stays bounded at 2 MiB × idle connections. |
| `temp_store` | default | No statement uses temporary storage (asserted: no `TEMP B-TREE` in any plan). |
| `foreign_keys` | default | The schema has no foreign keys. |

Connections are pooled by the engine (`SqliteSessionPool`; Microsoft.Data.Sqlite pooling is off).
Each idle connection keeps its prepared statements, so a statement is compiled once per connection,
not once per request. At most `ProcessorCount` connections stay idle per database and
`4 × ProcessorCount` in total, across every database. When the total is reached, pooling a
connection closes the least recently pooled one, whichever database it belongs to. A node with many
databases therefore keeps file handles and page caches (at most 2 MiB each, `cache_size`) only for
the databases in use. An extra connection opened under load is closed when it is returned.

One lock guards the idle connections, so a connection is idle, rented by one transaction, or
closed, never two at once. A deleted or shut-down database closes its idle connections, hands out
no new ones and closes rented ones when they come back. A disposed transaction refuses every
operation before it touches its connection, which may already belong to another transaction. The
engine's existing creation lock also serializes first opens with create, copy, delete and shutdown,
so a store is never published for a deleted file or after shutdown. Fast `StorageTransactionTests`
and Slow `StorageShutdownTests` cover these rules.

Writes are batched by transaction: `IStorageTransaction` accepts any number of writes before one
commit (one fsync). The document endpoints write one document per request (one fsync each); the
Management slice already groups its related writes in one transaction.

## Benchmark

Throwaway in-process harness (not in this repository; benchmarks belong in `ts-benchmark`)
compiling `Engine/` from `main` and from this change, 20 000 documents of ~1 KiB, one database,
local NVMe, `nice -n 19`. Mean of two runs; ranges where the runs disagreed.

| Case | Before | After |
| --- | --- | --- |
| Insert, 1 document per transaction | 4.8–7.0 ms | 4.6–4.9 ms with `synchronous = FULL` (fsync-bound; within noise); see below for `NORMAL` |
| Insert, 100 documents per transaction | 226 µs per document | 235 µs per document (within noise) |
| Point get, 1 thread | 20.9 µs | 12.6 µs |
| Point get, 8 threads | 5.6–8.3 µs per op | 3.0–3.4 µs per op |
| One list page (100) at a deep cursor | 8 590 µs | 110 µs |
| Page through the whole collection (100 per page) | 895 ms | 30 ms |

### Writes: `synchronous = FULL` versus `NORMAL` (decision 0003)

Same harness, same engine apart from the pragma, 20 000 documents of ~1 KiB, two alternating runs
of each.

| Case | `FULL` | `NORMAL` |
| --- | --- | --- |
| Insert, 1 document per transaction | 4.66–7.05 ms | 242–266 µs |
| Insert, 100 documents per transaction | 206–225 µs per document | 168 µs per document |
| Insert, 4 concurrent writers, 1 document per transaction | 6.10–6.83 ms per write | 141–151 µs per write |
| Update, 1 document per transaction | 5.44–6.84 ms | 91 µs |
