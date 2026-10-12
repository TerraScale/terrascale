# SQLite statements, query plans and connection settings

Scope: `src/TerraBase/Engine/Sqlite`. Plans are from `EXPLAIN QUERY PLAN` on the
Turso engine (`Turso.Data.Sqlite.Provider` 0.8.2; it labels a seek through a primary key's unique index
`USING INDEX sqlite_autoindex_…`, as every table is a rowid table). The Fast suite
(`Documents/QueryPlanTests.cs`) asserts the "after" plans for every statement in
`SqliteStatements.Shared`, `ClientOnly` and `SystemOnly`, so a statement that loses its index fails the build.

## Inventory

Only the engine runs SQL. Catalog and indexing have no SQL yet (their slices are interfaces only);
replication uses the engine journal statements below. API keys and the Management slice (organizations, projects, databases) are
documents in `_system` and use the document statements: lookups by id or unique name use `Get`, creates
and renames use `Put` and `Delete`, list endpoints and "has children" checks use `ListFirst` and `ListAfter`.
The plan test runs the shared statements against both a client database and the `_system` database, and each
kind's own statements against that kind (plans depend on the schema, not on the rows).

Schema version 1 bootstraps two shapes (`SqliteDatabaseStore`). A client database ([anna-core](design/anna-core.md)
Stored state): `documents (collection, id, state, version, body)`, `PRIMARY KEY (collection, id)`, `WITHOUT ROWID`,
where `state` is the document's encoded register, `version` the stamp of its last-writer-wins write and `body` that
write's JSON (NULL for a tombstone or a hidden uniqueness loser); `collections (name PRIMARY KEY, created_at, generation,
entity_id)`, `WITHOUT ROWID`; the one-row `commit_offset (id, value, incarnation)`, whose ULID incarnation is renewed for
newly created and copied databases. The primary keys are clustered B-trees, so every primary-key lookup is covering. The
partial `documents_live (collection, id) WHERE body IS NOT NULL` index excludes tombstones from live listing and query
scans; the partial `documents_replication_keys (collection, id)` index holds only nonempty keys within the 63-byte
collection and 1024-byte id cursor limits, for anti-entropy pages. Point reads and tombstone metadata still use the
primary key. The database's own stream is `changes (sequence, ordinal, collection, id, state)` (`WITHOUT ROWID`, primary
key `(sequence, ordinal)`) and `journal (sequence, dependencies)`; `applied (origin, sequence)` keys every other origin's
applied position, and the one-row `replication_state (id, replica, complete)` names the replica id the journal belongs
to. `shape_history (id, floor)` records the history baseline; `change_log` retains every write with collection
generation, document id, local history offset (`version`), document version (`document_version`) and sequence. Its
`change_log_by_offset` index supports the feed seeks described below.

`_system` keeps its catalog journal until TS-647 step 5: `documents (collection, id, version, body)` with `version` the
commit offset of the last write, the same `collections`, `commit_offset` and `documents_live`, its journal in
`changes (sequence, ordinal, collection, id, version, body)` with the partial `changes_deleted (collection, id, version)
WHERE body IS NULL` index and prefix digests in `journal_prefixes`, and `applied`, `replication_state` and the shape
tables unused. It retains no shape history. Both shapes include the uniqueness tables `_tb_unique_keys`, `_tb_unique_invalid` and
`_tb_unique_conflicts`.

| Statement | Hot path | Plan before | Plan after |
| --- | --- | --- | --- |
| `Get`: `SELECT … WHERE collection = ? AND id = ?` | system document reads, every API-key check | `SEARCH documents USING PRIMARY KEY (collection=? AND id=?)` | unchanged |
| `UniqueRawDocument` | client document reads, query row materialization and uniqueness reconciliation; source body, version and conflict flag in one statement | — | `SEARCH d USING PRIMARY KEY (collection=? AND id=?)`; `SEARCH c USING PRIMARY KEY (collection=? AND id=?) LEFT-JOIN` |
| `Version`: `SELECT version, typeof(body) = 'null' …` | write preconditions, delete, assert-absent | — | `SEARCH documents USING PRIMARY KEY (collection=? AND id=?)`; `typeof` reads the record header, not the body |
| `DocumentBytes`: retained current JSON length | quota net-change accounting without JSON allocation | — | a primary-key seek of `documents` and a `UNIQUE`-key seek of `_tb_unique_conflicts`; `octet_length` reads visible or hidden current JSON length without materializing JSON |
| `StoredDocumentBytes` | trusted storage-quota total, one scalar result | — | `SCAN collections`, then `CORRELATED SCALAR SUBQUERY 1` with `SEARCH d USING PRIMARY KEY (collection=?)` and a `UNIQUE`-key join of `_tb_unique_conflicts`; reads visible or hidden current sizes without JSON or collection-statistics allocation |
| `GetState`: `_tb_state` row with its materialized document | every client document write | — | `SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?)`; `LEFT-JOIN`s on the collection table's id key and on `_tb_unique_conflicts` |
| `ClientDocumentBytes`, `ClientListFirst`, `ClientListAfter` | client point sizes and listing over a collection table | — | id-key seeks or an in-order walk of `sqlite_autoindex_<collection>_1`; the version is one correlated `_tb_state` seek per row |
| `ClientCollectionStats`, `ClientConflictBytes`, `ClientCollectionNames` | collection counts and sizes, storage quota | — | one scan per collection table (sizes from record headers); `SCAN _tb_unique_conflicts`; `SCAN collections` |
| `UpdateRow`, `InsertRow`, `DeleteRow` | every client document write | — | id-key seek of the collection table; the insert has no plan rows |
| `UpdateState`, `InsertState`: one `_tb_state` row per document per request | the end of every client write | — | `SEARCH _tb_state USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?)`; the insert has no plan rows |
| `DropStates` | client collection drop | — | `SEARCH _tb_state USING INDEX sqlite_autoindex__tb_state_1 (collection=?)` |
| `Put`: `INSERT … ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = …` | every `_system` write | no plan rows (conflict check is a primary-key probe) | unchanged |
| `Tombstone`: `UPDATE … SET body = NULL …` | `_system` tombstone | — | `SEARCH documents USING PRIMARY KEY (collection=? AND id=?)` |
| `NextOffset`, `CurrentOffset` on `commit_offset WHERE id = 1` | first write of a transaction, commit without writes | — | `SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)` |
| `Incarnation`, `RenewIncarnation` on `commit_offset WHERE id = 1` | store open, new database/copy publication | — | `SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)` |
| `HasCollectionState` | fresh management catalog initialization (`_system`) | — | four `EXISTS` range seeks: `SEARCH documents USING PRIMARY KEY (collection>? AND collection<?)`, `SEARCH collections USING PRIMARY KEY (name>? AND name<?)`, `SEARCH changes USING INDEX changes_deleted (collection>? AND collection<?)` and `SEARCH change_log USING COVERING INDEX change_log_by_offset (collection>? AND collection<?)`; includes tombstones, retained replication deletions and shape history without reading bodies |
| `HasCollectionStateFrom` | collection prefixes with no Unicode successor | — | the same four indexed `EXISTS` seeks using only `collection>?` or `name>?`; a prefix composed entirely of maximum Unicode scalars has no upper bound |
| `CreateCollection`, `GetCollection`, `DropCollection` | collection routes, every document request (existence check) | — | insert: no plan rows; others `SEARCH collections USING PRIMARY KEY (name=?)` |
| `ListCollectionsFirst`, `ListCollectionsAfter` | collection listing | — | `SCAN collections` (primary-key order, stops at the limit); `SEARCH collections USING PRIMARY KEY (name>?)` |
| `DropDocuments`: `DELETE … WHERE collection = ?` | collection drop | — | `SEARCH documents USING PRIMARY KEY (collection=?)` |
| `Delete`: `DELETE … WHERE collection = ? AND id = ?` | every `_system` delete | `SEARCH documents USING PRIMARY KEY (collection=? AND id=?)` | unchanged |
| List, first page: `… WHERE collection = ? ORDER BY id LIMIT ?` | list without cursor | `SEARCH documents USING PRIMARY KEY (collection=?)` | `SEARCH documents USING INDEX documents_live (collection=?)` (`ListFirst`), skipping tombstones |
| List, next page: `… WHERE collection = ? AND id > ? ORDER BY id LIMIT ?` | list with cursor | `SEARCH documents USING PRIMARY KEY (collection=?)` — one statement used `($after IS NULL OR id > $after)`, which kept SQLite from seeking to the cursor: every page walked the collection from its first id | `SEARCH documents USING INDEX documents_live (collection=? AND id>?)` (`ListAfter`) |
| `ObserveOffset`, `Origin` | a catalog follower's offset, the `_system` stream identity | — | integer-primary-key search on `commit_offset` |
| `ClientHasCollectionState`, `ClientHasCollectionStateFrom` | collection state checks on a client database | — | three `EXISTS` range seeks of `documents`, `collections` and `change_log_by_offset`: a client database keeps a drop's tombstones as registers, so it has no journal deletions to look at |
| `LastSequence`, `AppendReplicationChange`, `GetChangeDigest`, `AppendChangeDigest` | `_system` journal append | — | journal primary-key search; inserts have no plan rows |
| `PRAGMA user_version`, `PRAGMA journal_mode = WAL`, `CREATE TABLE` | once per database per process (bootstrap) | DDL, no plan | unchanged |
| `VACUUM INTO $path` | database copy (Management API), rare | maintenance, `EXPLAIN QUERY PLAN` returns no rows | unchanged; runs on a pooled connection |
| `CountDocuments`: `SELECT count(*) … WHERE collection = ? AND body IS NOT NULL` | schema inference, query-field records in `_system` (Management API) | — | `SEARCH documents USING PRIMARY KEY (collection=?)`; tests each record header's body type, never loads a body. `documents_live` would not cover the `body` test and would add a lookup per row |
| `ListCollectionStats` | usage, collection list (Management API) | — | `SCAN collections` in key order, then per collection `SEARCH documents USING PRIMARY KEY (collection=?)` (a `LEFT-JOIN` summing `octet_length(body)`, which reads the size from the record header) |
| `DeleteCollectionDocuments`, `DeleteCollectionNames`: `DELETE … WHERE name >= ? AND name < ?` | database or project deletion in `_system` (Management API) | — | `SEARCH documents USING PRIMARY KEY (collection>? AND collection<?)`; `SEARCH collections USING PRIMARY KEY (name>? AND name<?)` |
| `ListIndexes`, `IndexExists` on `sqlite_schema` | index list, create and drop (Management API) | — | `SCAN sqlite_schema` (a handful of rows); the list is sorted in memory, not in a temporary B-tree |
| `SystemDocumentsFirst`, `SystemDocumentsAfter` | catalog snapshot pages in `_system` (catalog primary, [0009](decisions/0009-catalog-replication.md)) | — | `SCAN documents USING INDEX documents_live` under LIMIT; `SEARCH documents USING INDEX documents_live ((collection,id)>(?,?))` |
| `GroupSize`, `ReadGroupPage` | catalog group pages in `_system` | — | `SEARCH changes USING PRIMARY KEY (sequence=?)`; `SEARCH changes USING PRIMARY KEY (sequence=? AND ordinal>?)` |
| `CollectionRangeIds` | captured prefix drop in `_system` | — | `SEARCH documents USING PRIMARY KEY (collection>? AND collection<?)`, ids only |
| `ReplaceDocument` | every replicated catalog change on a follower | — | upsert, no plan rows |
| `SystemLastSequence`, `TruncateChanges`, `TruncatePrefixes` | every `_system` commit (journal head and its 1024-group tail) | — | `SEARCH journal_prefixes USING PRIMARY KEY`; `SEARCH changes USING PRIMARY KEY (sequence<?)`; `SEARCH journal_prefixes USING PRIMARY KEY (sequence<?)` |

No fixed statement sorts in a temporary B-tree. The first collections, baseline and anti-entropy pages scan in key order
up to their limits; anti-entropy pages read the partial key index, so excluded empty or oversized keys consume no scan
work, and the reader skips other inadmissible keys before decoding their registers. A copy clears its journal and applied
positions in the same transaction that renews its incarnation; its baseline then carries every register, tombstones
included, under its fresh origin.

### Shape history (TS-474 schemas v5/v6, replication integration v7)


Writes to client databases append to `change_log`; the per-collection change feed reads it (`change-feeds.md`).
Shape reads and their snapshot and log-chunk statements were deleted in R2c-1; the `change_log_births` and
`change_log_high` tables, their trigger and `change_log_by_document` have no readers and go in R2c-2. `Documents/QueryPlanTests.cs` asserts these plans.

| Statement | Hot path | Plan |
| --- | --- | --- |
| `AppendChange`: `INSERT … SELECT … FROM collections WHERE name = ?` | every client-database write | `SEARCH collections USING PRIMARY KEY (name=?)` |
| `HistoryFloor` | every shape read past the snapshot redirect | `SEARCH shape_history USING INTEGER PRIMARY KEY (rowid=?)` |
| `DropHistory`: `UPDATE change_log SET body = NULL …` | collection drop | `SEARCH change_log USING INDEX change_log_by_offset (collection=? AND generation=?)` |

Queries and counts (`SqliteQueryCompiler`) are compiled per request and are neither prepared on
the session nor in `SqliteStatements.All`. Live queries read the collection's table (by its id key with an id-only
cursor) and read values with `json_extract(doc, '$.path')` only, never `->>`; `json_type` guards the JSON type as a
residual term. A path that is a valid field is embedded as that literal, so the Management API's expression indexes
(below) serve equality, ranges, `in` of up to 64 values (equalities joined by a balanced `OR` tree, a
`MULTI-INDEX OR` plan; a longer list is one JSON-array parameter read with `json_each`, which scans) and
`prefix` (the range `>= prefix AND < successor`, UTF-8 byte order); the Turso planner uses an index for neither `IN`
nor a prefix test (docs/terrabase/turso-spike.md). A scan budget is a plain `AND` term, so it does not hide the
filter from the planner. A sort on a document field orders by JSON type first, so it sorts in a temporary B-tree.
`IndexPlanTests` checks these plans, and the SQL subset's equality, range, `IN`, prefix `LIKE` and
`ORDER BY … LIMIT`, against the real server database. Queries/counts have cancellation,
deadline and candidate budgets; field sorts also preflight
10000 live candidates and 8 MiB of JSON metadata before admitting a temporary sorter. A leading `$id` sort emits only
`ORDER BY id ASC|DESC`: the constant rank and redundant later terms would force a sorter. Its
ascending/descending and duplicate-key/cursor plans are checked against the real server database.
Explicit `$id` continuation predicates are direct `id > ?` or `id < ?` ranges, so the live index seeks
past prior pages. Count evaluates the filter only on the first bounded rows, with a separate metadata
scan of at most bound + 1 rows to detect overflow without adding that row to the match sum.
The commit-offset increment is guarded by `value < 9223372036854775807`; exhaustion cannot promote
its integer counter to REAL.

## Connection settings

Most settings are applied once per connection (`SqliteSession`). Read transactions additionally
enable `query_only = ON` before BEGIN and reset it to OFF after the transaction ends; a reset
failure closes the session instead of pooling it.

TerraBase keeps Turso's defaults (user guidance 2026-10-12: Turso defaults and best practices): WAL journal, 4 KiB
pages, Turso's own checkpoints, busy timeout 0, no memory map (Turso has none), default temporary storage, no foreign
keys. Every setting that differs, and why:

| Setting | Value | Turso default | Why |
| --- | --- | --- | --- |
| `synchronous` | `NORMAL` | `FULL` | A local write is acknowledged after one machine's commit (decision 0003, `multi-region-replication.md` section 6): a commit does not wait for an fsync, so acknowledged writes can be lost on power loss or an OS crash, never on a process crash, and the file is not corrupted. Durable write transactions and replicated applies set `FULL` for their transaction. The connection that creates a file uses `OFF`: the file is private until it is published and synced once then. |
| `cache_size` | ¼ of `memory-budget` ÷ `storage-threads`, at least 2 MiB | 2000 pages | Turso has no process-wide heap limit, so per-connection caches sized from the budget are the memory bound (`memory.md`). |
| `query_only` | `1` in read transactions | `0` | Read-only transactions cannot write; reset to `0` before the session is pooled. |
| `capture_data_changes_conn` | `after` on the writer | off | Design C's change capture and continuous backup (`backup.md`); a transaction may turn it off (`SuspendCaptureAsync`). |
| `wal_checkpoint(TRUNCATE)` | when a file is created, restored, copied, and when its writer closes | not run | Turso does not checkpoint when the last connection closes, and these files are moved or copied without their `-wal`. |

Read connections are pooled by the engine (`SqliteSessionPool`; provider pooling is off). Each database also has one
writer connection, outside the pool, that every write transaction uses: Turso aborts the process when two connections
write one file. Turso reads planner statistics only when a connection opens, so after an index build every older
connection, the writer too, is closed when it is next returned instead of pooled.
Each idle connection keeps its prepared statements, so a statement is compiled once per connection,
not once per request. At most `ProcessorCount` connections stay idle per database and
`4 × ProcessorCount` in total, across every database. When the total is reached, pooling a
connection closes the least recently pooled one, whichever database it belongs to. A node with many
databases therefore keeps file handles and page caches (`cache_size` each; Turso has no process-wide heap limit) only
for the databases in use. An extra connection opened under load is closed when it is returned.

One lock guards the idle connections, so a connection is idle, rented by one transaction, or
closed, never two at once. A deleted or shut-down database closes its idle connections, hands out
no new ones and closes rented ones when they come back. A disposed transaction refuses every
operation before it touches its connection, which may already belong to another transaction. The
engine's existing creation lock also serializes first opens with create, copy, delete and shutdown,
so a store is never published for a deleted file or after shutdown. Fast `StorageTransactionTests`
and Slow `StorageShutdownTests` cover these rules.

Writes are batched by transaction: `IStorageTransaction` captures at most 1024 document changes and 2 MiB before one
commit (one fsync). The document endpoints write one document per request (one fsync each); the
Management slice already groups its related writes in one transaction.

## Benchmark

Throwaway in-process harness (not in this repository; benchmarks belong in `benchmark/`)
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
- SQL-subset queries share the document query work budget (`QueryBudget` in `SqliteStorageTransaction`: 5 seconds,
  or cancellation). The seconds are `query-budget-seconds` (default 5); the test harness raises them so that a loaded runner's
  descheduling or GC pauses do not fail tiny queries, and tests of the time budget pin the default.

`_system` schema 8 stores its catalog journal's prefix SHA-256 values in `journal_prefixes` (sequence primary key);
`GetChangeDigest` seeks it by sequence. Client databases keep no prefix digests: an origin never reuses a sequence.
