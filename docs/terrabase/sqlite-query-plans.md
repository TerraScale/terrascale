# SQLite statements, query plans and connection settings

Scope: `src/TerraScale.TerraBase/Engine/Sqlite`. Plans are from `EXPLAIN QUERY PLAN` on the
SQLite that Microsoft.Data.Sqlite bundles (3.53.4). The Fast suite
(`Documents/QueryPlanTests.cs`) asserts the "after" plans for every statement in
`SqliteStatements.All`, so a statement that loses its index fails the build.

## Inventory

Only the engine runs SQL. Catalog and replication have no SQL yet (their slices are interfaces
only). API keys and the Management slice (organizations, projects, databases) are
documents in `_system` and use the document statements: lookups by id or unique name use `Get`, creates
and renames use `Put` and `Delete`, list endpoints and "has children" checks use `ListFirst` and `ListAfter`.
The plan test runs every statement against both a client database and the `_system` database
(plans depend on the schema, which is the same in both, not on the rows).

Schema v4: `documents (collection, id, version, body)`, `PRIMARY KEY (collection, id)`,
`WITHOUT ROWID`, where a NULL `body` is a tombstone and `version` is the commit offset of the last
write; `collections (name PRIMARY KEY, created_at)`, `WITHOUT ROWID`; and the one-row
`commit_offset (id, value, incarnation)`; the random UUIDv4 incarnation is persisted and renewed
for newly created/copied databases. The primary keys are clustered B-trees, so every primary-key lookup is covering.
The partial `documents_live (collection, id) WHERE body IS NOT NULL` index excludes tombstones
from live listing and query scans. Point reads and tombstone metadata still use the primary key.

| Statement | Hot path | Plan before | Plan after |
| --- | --- | --- | --- |
| `Get`: `SELECT … WHERE collection = ? AND id = ?` | every document read, every API-key check | `SEARCH documents USING PRIMARY KEY (collection=? AND id=?)` | unchanged |
| `Version`: `SELECT version, typeof(body) = 'null' …` | write preconditions, delete, assert-absent | — | `SEARCH documents USING PRIMARY KEY (collection=? AND id=?)`; `typeof` reads the record header, not the body |
| `Put`: `INSERT … ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = …` | every document write | no plan rows (conflict check is a primary-key probe) | unchanged |
| `Tombstone`: `UPDATE … SET body = NULL …` | document delete (Documents API) | — | `SEARCH documents USING PRIMARY KEY (collection=? AND id=?)` |
| `NextOffset`, `CurrentOffset` on `commit_offset WHERE id = 1` | first write of a transaction, commit without writes | — | `SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)` |
| `Incarnation`, `RenewIncarnation` on `commit_offset WHERE id = 1` | store open, new database/copy publication | — | `SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)` |
| `CreateCollection`, `GetCollection`, `DropCollection` | collection routes, every document request (existence check) | — | insert: no plan rows; others `SEARCH collections USING PRIMARY KEY (name=?)` |
| `ListCollectionsFirst`, `ListCollectionsAfter` | collection listing | — | `SCAN collections` (primary-key order, stops at the limit); `SEARCH collections USING PRIMARY KEY (name>?)` |
| `DropDocuments`: `DELETE … WHERE collection = ?` | collection drop | — | `SEARCH documents USING PRIMARY KEY (collection=?)` |
| `Delete`: `DELETE … WHERE collection = ? AND id = ?` | every document delete | `SEARCH documents USING PRIMARY KEY (collection=? AND id=?)` | unchanged |
| List, first page: `… WHERE collection = ? ORDER BY id LIMIT ?` | list without cursor | `SEARCH documents USING PRIMARY KEY (collection=?)` | `SEARCH documents USING INDEX documents_live (collection=?)` (`ListFirst`), skipping tombstones |
| List, next page: `… WHERE collection = ? AND id > ? ORDER BY id LIMIT ?` | list with cursor | `SEARCH documents USING PRIMARY KEY (collection=?)` — one statement used `($after IS NULL OR id > $after)`, which kept SQLite from seeking to the cursor: every page walked the collection from its first id | `SEARCH documents USING INDEX documents_live (collection=? AND id>?)` (`ListAfter`) |
| `PRAGMA user_version`, `PRAGMA journal_mode = WAL`, `CREATE TABLE` | once per database per process (migration) | DDL, no plan | unchanged |
| `VACUUM INTO $path` | database copy (Management API), rare | maintenance, `EXPLAIN QUERY PLAN` returns no rows | unchanged; runs on a pooled connection |
| `CountDocuments`: `SELECT count(*) … WHERE collection = ? AND body IS NOT NULL` | schema inference, query-field records in `_system` (Management API) | — | `SEARCH documents USING PRIMARY KEY (collection=?)`; tests each record header's body type, never loads a body. `documents_live` would not cover the `body` test and would add a lookup per row |
| `CountCollections`, `ListCollectionStats` | usage, collection list (Management API) | — | `SCAN collections` in key order, then per collection `SEARCH documents USING PRIMARY KEY (collection=?)` (a correlated count, or a `LEFT-JOIN` summing `octet_length(body)`, which reads the size from the record header) |
| `DeleteCollectionDocuments`, `DeleteCollectionNames`: `DELETE … WHERE name >= ? AND name < ?` | database or project deletion in `_system` (Management API) | — | `SEARCH documents USING PRIMARY KEY (collection>? AND collection<?)`; `SEARCH collections USING PRIMARY KEY (name>? AND name<?)` |
| `ListIndexes`, `IndexExists` on `sqlite_schema` | index list, create and drop (Management API) | — | `SCAN sqlite_schema` (a handful of rows); the list is sorted in memory, not in a temporary B-tree |

No fixed statement sorts in a temporary B-tree, and only the first collections page scans (in key
order, up to its limit).

### Shape history (TS-474, schemas v5 and v6)

Writes to client databases append to `change_log`; shape reads replay it (`docs/shape-reads.md`).
`Documents/QueryPlanTests.cs` asserts these plans, including that neither shape read scans or sorts.

| Statement | Hot path | Plan |
| --- | --- | --- |
| `AppendChange`: `INSERT … SELECT … FROM collections WHERE name = ?` | every client-database write | `SEARCH collections USING PRIMARY KEY (name=?)` |
| `HistoryFloor` | every shape read past the snapshot redirect | `SEARCH shape_history USING INTEGER PRIMARY KEY (rowid=?)` |
| `DropHistory`: `UPDATE change_log SET body = NULL …` | collection drop | `SEARCH change_log USING INDEX change_log_by_offset (collection=? AND generation=?)` |
| Snapshot at `S` (`CompileSnapshot`, one streamed statement per page) | every snapshot page | `SEARCH b USING PRIMARY KEY (collection=? AND generation=? AND id>? AND id<?)` on `change_log_births` in id order up to the highest id at `S` (`SEARCH high USING PRIMARY KEY (collection=? AND generation=? AND version<?)`, once per page), no sort (`version <= S` is tested on each key entry: an id first written later costs one entry, its revisions never read; the page reads at most 10000 such steps and 50000 later ids), per eligible id `SEARCH last USING COVERING INDEX change_log_by_document (collection=? AND generation=? AND id=? AND version<?)` for its last write at `S`, then `SEARCH change_log USING INTEGER PRIMARY KEY (rowid=?)` for that write |
| Log chunk (`CompileChanges`) | every log chunk and live wake | `SEARCH c USING INDEX change_log_by_offset (collection=? AND generation=? AND version>? AND version<?)`, then per row `SEARCH last USING COVERING INDEX change_log_by_document (collection=? AND generation=? AND id=? AND version=?)`: only a document's last write of each commit is sent |

A snapshot page walks document ids in order, so its cost follows the ids it reads, not
the depth of each document's history (a single-statement form scanned every history row and, without
`INDEXED BY`, sorted all candidates in a temporary B-tree). The last-write lookup names `change_log_by_document`.

Queries and counts (`SqliteQueryCompiler`) are compiled per request and are neither prepared on
the session nor in `SqliteStatements.All`. Live queries read the collection's partial-index range
(`SEARCH documents USING INDEX documents_live (collection=?)`, or `collection=? AND id>?` with an id-only
cursor) and evaluate filters with `json_type`/`json_extract`/`json_each`; a sort on a document field
sorts in a temporary B-tree. Document queries do not use the Management API's expression indexes (below). Queries/counts have cancellation,
VM-instruction and deadline budgets; field sorts also preflight
10000 live candidates and 8 MiB of JSON metadata before admitting a temporary sorter. A leading `$id` sort emits only
`ORDER BY id ASC|DESC`: the constant rank and redundant later terms would force a sorter. Its
ascending/descending and duplicate-key/cursor plans are checked against the real server database.
Explicit `$id` continuation predicates are direct `id > ?` or `id < ?` ranges, so the live index seeks
past prior pages. Count evaluates the filter only on the first bounded rows, with a separate metadata
scan of at most bound + 1 rows to detect overflow without adding that row to the match sum.
The commit-offset increment is guarded by `value < 9223372036854775807`; exhaustion cannot promote
its integer counter to REAL.

## Connection settings

Applied once per connection (`SqliteSession`), not per transaction.

| Setting | Value | Why |
| --- | --- | --- |
| `journal_mode` | `WAL` | Persistent in the file (set at migration). Readers never block the writer. |
| `synchronous` | `NORMAL` | Decision 0003 (`docs/decisions/0003-sqlite-synchronous-normal.md`). A commit no longer waits for an fsync; SQLite syncs the WAL before each checkpoint. Committed transactions, including acknowledged writes, can be lost after a power loss or an OS crash, but not after a process crash, and the file is never corrupted. Accepted for write throughput. Every connection uses it, including the one that creates a database. |
| `busy_timeout` | 30 s | SQLite's own busy handler (1–100 ms back-off) instead of Microsoft.Data.Sqlite's retry loop, which sleeps 150 ms between attempts: a writer behind a 20 ms write lock waited 150 ms before, 34 ms after. Same 30 s bound as the command timeout. |
| `mmap_size` | 256 MiB | Reads map pages instead of copying them through `read()`: about 5–10 % faster point reads and 25 % faster full listings in the benchmark below. Address space only; pages are the OS page cache. |
| `cache_size` | default (2 MiB) | With mmap, reads bypass most of the page cache. Per-connection memory stays bounded at 2 MiB × idle connections. |
| `temp_store` | default | No fixed statement uses temporary storage (asserted: no `TEMP B-TREE` in any plan); a query sorted on a document field does. |
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

## Management API: SQL subset and secondary indexes

`SqliteStorageTransaction.Queries.cs` compiles the dashboard SQL subset (TS-463) per request and creates secondary
indexes; neither is in `SqliteStatements.All`. Both read live documents only.

- A SQL-subset query is `SELECT id, version FROM documents WHERE collection = '<collection>' AND body IS NOT NULL
  AND <filter> ORDER BY <fields>, id LIMIT ?`, with `id DESC` when the first field is descending, so an index read
  backwards serves the whole order. A temporary sort holds keys only; each body is read by primary key as its row is
  consumed. Fields compile to `id`, `version` or `json_extract(body, '$.<path>')`; the collection and the paths are
  literals so that SQLite matches an expression index textually.
- An index is `CREATE INDEX "tb_index_<name>" ON documents (collection, <fields>) WHERE collection = '<collection>' AND
  body IS NOT NULL`: partial on the same two terms the query repeats, so tombstones take no index space and the planner
  can use it. Document queries never use it (they name `documents_live`), nor do document counts, which read
  tombstones too.
- After a build, `ANALYZE` of the new index alone and a seeded or raised `sqlite_stat1` row for the primary key and
  `documents_live` (`"<rows> <rows> 1"`) keep the planner from preferring either collection-leading index over it. Plans
  are reported by `POST …/sql/check` and asserted by the Fast `SqlApiTests` and `IndexApiTests`.
- SQL-subset queries share the document query work budget (`QueryBudget` in `SqliteStorageTransaction`: 5 million VM instructions or 5 seconds,
  or cancellation).
