# Multi-region replication on Turso (design C)

- Status: design, 2026-10-11; slices land as listed in section 21. Each guarantee holds only once its slice ships
  with integration and e2e evidence.
- Decision: design C (user, 2026-10-11). Turso is the storage engine, Turso CDC is the change capture, and TerraBase's
  own Anna-style lattices and gRPC streams do the replication, over Fly 6PN. Turso's sync engine is not used: it
  resolves by last push and its self-hosted hub is test-only.
- Inputs: `.cos/fly-migration-plan.md` (Decisions, Product focus), the Turso spike (PR #12,
  `docs/terrabase/turso-spike.md`), phase 3a (PR #11, `refactor/fly-wan-grpc`), the e2e suite (PR #6), and
  [Anna core](design/anna-core.md). Where they conflict, this document replaces anna-core §3 to §9 and decisions
  [0009](decisions/0009-catalog-replication.md) and [0010](decisions/0010-write-wait-and-synced-durable-ack.md).

## 1. Summary

Every database is one Turso file on every machine of every region in its placement. Any machine accepts any write and
answers after its own local commit. A document is a map of fields. Each field merges on its own: the newest
`(HLC stamp, node)` wins. The document has a clear register for PUT and DELETE. Fields can also be PN-counters or
add-wins sets. All of this state is a join-semilattice, so a replica can apply deltas in any order, any number of
times, and every replica ends in the same state.

```text
 client ─HTTP─> machine (gru)                                           machine (sjc)
                 │ write lane (the only writer connection, Turso WAL + CDC)
                 │   op -> stamp S -> merge into DocumentState -> collection row + _tb_state row
                 │   commit -> 200
                 ▼
               turso_cdc ──tailer──> change groups (one per Turso txn, local-origin rows only)
                 │                     │                          ┌─> receiver: join, materialize, ack
                 │                     └──gRPC Push over 6PN──────┘   (origin = gru, never re-shipped)
                 ├──tailer──> _tb_feed (per-origin index) ──> GET /databases/{id}/changes (SSE)
                 └──_tb_merkle (4096 buckets) <── anti-entropy Digest/ReadBucket ──> peers
```

- **Writes** are leaderless. There is no `If-Match`, no compare-and-set, no primary and no forwarding.
- **Replication** is a direct per-origin stream from each machine to every other replica of the database, in every
  region. A machine never forwards another machine's changes, so changes never echo back.
- **Anti-entropy** compares Merkle buckets and repairs whatever the streams missed. It also seeds a new or refilled
  replica.
- **The catalog** (`_system`) is a lattice database replicated to every machine. The catalog primary goes.
- **SQL** can write. A statement is parsed, its `WHERE` runs against local state, and the result becomes field
  deltas, so SQL writes merge like API writes.
- **The changes feed** comes from CDC. Its cursor is a vector of per-origin stamps, so it is valid on any machine.

## 2. Turso 0.8.2 constraints this design obeys

From the spike (PR #12 and its raw output). Each one is a hard rule for every slice.

| Fact (0.8.2) | Rule here |
| --- | --- |
| Two writer connections on one file abort the process (`wal.rs:3590`) | One writer connection per file: the write lane. Local writes, replicated applies, the tailer's checkpoint and CDC deletes, GC and DDL all run on it. Readers use the read pool. |
| `WITHOUT ROWID` cannot UPDATE, DELETE or be indexed, and CDC ignores it | Rowid tables only. Keys are `UNIQUE` columns. |
| CDC `id` holds the rowid, not the key | Consumers read keys from the after-image. |
| `bin_record_json_object` cannot format BLOB values | Captured tables have no BLOB columns. Encoded state is base64url TEXT. |
| For `ON CONFLICT DO UPDATE`, CDC `before` holds the new row. `INSERT OR REPLACE` is captured as a delete plus an insert | Captured tables use plain `INSERT`, `UPDATE` and `DELETE`. Merges happen in C#. Nothing here needs `before` images. |
| CDC is per connection. Rows land in the same transaction and are visible to that connection before commit. Rolled-back work leaves no rows. `change_txn_id` groups a transaction and type 2 marks its commit | Groups follow Turso transactions. |
| An empty `turso_cdc` restarts `change_id` at 1 after reopen | Truncation always keeps the newest row. |
| `capture_data_changes_conn` takes the write lock and ignores `busy_timeout` | Set it once when the lane opens, and retry on "database is locked". |
| Full mode writes each updated row three times | Mode `after` (section 7). |
| No statement cancellation and no progress handler | SQL keeps a bounded grammar (section 12). |
| The planner prefers an overlapping partial index over an expression index | Per-collection tables remove the overlapping `documents_live` partial index. Slice R2b measures this. |
| MVCC + CDC works on a database already in `mvcc` mode, but the poller can skip interleaved groups and CDC is a conflict hotspot | Stay on WAL. `BEGIN CONCURRENT` is section 22. |

The spike's "CDC costs about 2 %" figure is invalid: those runs never enabled CDC. The only valid figure is about
1215 → 766 upserts/s (−37 %) in WAL. The design keeps captured rows per operation to two (the collection row and the
`_tb_state` row). Slice R3 measures this on a Fly `shared-cpu-2x` machine before R4 builds on it.

## 3. Identity, HLC and stamps

- **Node id** = `ReplicaId` (64 bits: region 16, member 32, epoch 16). The member number is random and lives in
  `.terrabase.replica` on the volume. The epoch rises on every unclean start (kv679 protection, unchanged). The region
  bits become the Fly region code as base 26 (`a`=0, so `gru` = 6·26² + 17·26 + 20 = 4518; three letters < 17,576).
  A node id is therefore unique per machine, per epoch.
- **Stamp** = `Hlc` (48-bit Unix ms, 16-bit logical), unchanged. A version is `(stamp, node)`, compared
  lexicographically. Two different events never share a version. A node mints each stamp at most once. A new epoch
  is a new node, so stamps lost in a crash are never reused.
- **One operation = one stamp.** A document write, a whole `POST /transactions` request, a SQL statement chunk
  (section 12) or a management commit gets one stamp `S`. Every entry it writes carries `(S, self)`.
- **Minting.** The write lane mints `S = max(clock.Tick(), maxStamp(doc) + 1)` for each touched document. Stamps are
  minted inside the single writer, so an origin's commit order equals its stamp order. The changes feed and GC rely on
  this (sections 11 and 14).
- **Observing.** Every applied stamp goes through `MemberClock.Observe`, which caps it at now + 1 s (`MaxSkew`, a code
  constant) and counts `terrabase.clock.skew_capped`. The stored stamp is never changed. The per-document `+1` keeps
  read-your-writes even after a remote stamp from the future.
- **Skew bound.** Fly hosts run NTP. We expect skew of a few ms, though nobody has measured it on Fly yet. Within the
  skew, last-writer-wins between concurrent writes may pick the writer that is slightly earlier in real time. That is
  the documented behaviour. Past 1 s the metric alerts. Nothing rejects the stamp, because rejecting at one replica
  and not another would break convergence.
- **New origins.** A machine accepts writes only after two things: its own id appears in `vms.ts-terrabase.internal`
  and one heartbeat (5 s) has passed. By then every peer's directory has listed it, so GC holds for it (section 11).

## 4. Document lattice

```text
DocumentState = { clear: Version+deleted?,  fields: name -> FieldState }
FieldState    = { base:     Version, value (raw JSON) | tombstone       -- LWW, also the field's epoch
                  counters: node -> (epoch: Version, inc: u64, dec: u64) -- PN-counter
                  dots:     (element, Version) -> removedAt: Version?   -- add-wins set }
Version       = (stamp: Hlc, node: ReplicaId)
```

**Join** (all associative, commutative, idempotent):

| Entry | Join |
| --- | --- |
| `clear` | larger version, with its `deleted` bit |
| `base` | larger version, with its value |
| `counters[node]` | larger `epoch`. On an equal epoch, pointwise max of `inc` and `dec` |
| `dots[(element, version)]` | union. `removedAt` = the larger of the two (absent < any) |

**Visibility and value.**

- A field's **epoch** is `base.version` when it has a base, otherwise `clear.version`.
- A field is **live** when `base.version ≥ clear.version` (or it has no base), and one of these holds:
  - its base is not a tombstone;
  - it has a live counter entry (`epoch` equal to the field's epoch);
  - it has a live dot (version ≥ epoch, not removed).
- The **document exists** when `clear` is present and not deleted, or when a live base or live dot has a version above
  `clear.version`. A write after a delete brings the document back with only the fields written after the delete.
  This is last-writer-wins at document level. It also stops a stale concurrent PATCH from bringing old fields back.
- **Value of a live field**, in order:
  1. If live dots exist, or the base is an array and the field has dots: a set. Its elements are the base's elements
     without an explicit dot of their own, plus every explicit dot not removed, distinct and sorted by ordinal
     canonical JSON. Removing every element of an array-based set therefore leaves `[]`, not the old array.
  2. Otherwise, if live counter entries exist: the base number (0 if the base is absent or a tombstone) plus
     Σ(inc − dec). This is a 64-bit integer: increments are integers.
  3. Otherwise: the base value.

  Concurrent `inc` and `add` on the same absent field therefore make a set. Local validation rejects `inc` on a
  non-number and `add` on a non-array base.
- **Fields are top-level keys.** A nested path (merge-patch into an object, `address.city` in SQL) is a
  read-modify-write of its top-level field: the whole top-level value is LWW. `// anna-full`: nested maps as nested
  `DocumentState`s.

**Operations:**

| API | Entries written at `(S, self)` |
| --- | --- |
| `PUT` / create | `clear = (S, live)`, then `base` for every key of the body |
| `DELETE` | `clear = (S, deleted)` |
| PATCH, merge-patch key = value | `base` = value |
| PATCH, merge-patch key = null; ops `unset` | `base` = tombstone |
| ops `set`, `append` | `base` = the new value. `append` is a read-modify-write of the whole array, so it is LWW |
| ops `inc n` (an int64, may be negative) | own `counters[self]`: reset to `(epoch, 0, 0)` when its epoch is old, then `inc += n` or `dec += −n` |
| ops `add e` / `remove e` | `add`: new dot `(e, (S, self))`. `remove`: `removedAt = S` on every dot of `e` this node sees. The elements of an array base are implicit dots at `base.version`: nothing is copied, and removing one writes the explicit dot `(e, base.version)` with `removedAt = S`. Every node reads the same implicit dots from the same base, so this is deterministic, even on a node whose first set op was not the conversion |

Every PATCH, including `inc`, `add` and `remove`, needs a document that exists locally (404 `not_found`, as today).
A concurrent `inc` against a delete therefore lands in the old epoch, and the delete wins. JSON Pointer paths keep
working for `set`, `unset` and `append`, as a read-modify-write of their top-level field. `inc`, `add` and `remove`
take top-level fields only. `inc` becomes integer-only (today it uses exact decimal arithmetic).

**Normalization.** After every write or join, entries the state already dominates are dropped:

- base entries below `clear`;
- counter entries of an older epoch;
- dots below their field's epoch.

Dominance only grows under join, so normalization commutes with join. Tombstoned bases, removed dots and deleted
`clear`s are not dominated. They wait for GC (section 11).

**Delta of an operation** = the entries with `changed = S`, plus the values of plain fields. `changed` is local
bookkeeping: it is encoded but not merged and not hashed. A later operation in the same transaction re-sends what it
overwrote, so nothing is lost. Several operations of one request on one document share `S` and apply in order, the
later one replacing what the earlier one wrote at `S` (a `set` after an `add` drops that dot). So the lane writes the
document's rows once per request, after its last operation: an intermediate image would ship an entry at `S` that the
final state no longer holds, and no join could take it back.

**Materialization** is a pure function of state plus values. Fields are sorted by ordinal UTF-8 name. Values are
compact canonical JSON (re-serialized once at write time), counters are integers and sets are sorted arrays. Every
replica therefore holds the same bytes for the same state. Key order of a PUT body is not kept (JSON objects are
unordered).

**Encoding** (`Engine/Lattices/DocumentStateEncoding`) follows the `LatticeEncoding` rules: one format byte (1),
canonical order, minimal LEB128 and bounded counts. It is a trust boundary and accepts canonical bytes only. A per-
state node dictionary keeps it small. It is stored as base64url TEXT. It has two forms: the full form carries every
value (deltas), and the stored form leaves out a plain field's value, which the collection row's `doc` already holds
(`_tb_state.state`); decoding the stored form needs that `doc` and checks that it is the state's materialization. The
digest is SHA-256 over the stored form with `changed` zeroed, then `doc`. A version is the type `EntryVersion` (the
name `Version` would collide with `System.Version`). `GCounter` and the old `AwSet` went with R1; `MvRegister`,
`CausalContext`, `Dot` and `DocumentWrite` go with R2a, which removes their last callers (section 19). `Hlc`,
`ReplicaId`, `ILattice`, `MaxLattice`, `LwwRegister`, `MapLattice` and `SetLattice` stay.

## 5. Storage layout (one Turso file per database)

```sql
-- One rowid table per collection: what reads, queries, indexes and SQL see.
CREATE TABLE "<collection>" (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);
CREATE INDEX "_tb_ix_<name>" ON "<collection>" (json_extract(doc, '$.<field>'), ...);   -- from _indexes

-- Replicated lattice state, one row per document (live or deleted).
CREATE TABLE _tb_state (
  collection TEXT NOT NULL, id TEXT NOT NULL,
  bucket INTEGER NOT NULL,              -- first 12 bits of SHA-256(len-prefixed collection, id): 0..4095
  state  TEXT NOT NULL,                 -- base64url DocumentState: metadata, counter bases, set elements
  stamp  INTEGER NOT NULL,              -- the last operation applied to this row (bookkeeping, not merged)
  origin INTEGER NOT NULL,              -- that operation's node; 0 for an anti-entropy repair
  live   INTEGER NOT NULL,              -- 1 when "<collection>" holds the document
  h1 INTEGER NOT NULL, h2 INTEGER NOT NULL,   -- 128-bit digest of (state without `changed`, doc)
  UNIQUE (collection, id));
CREATE INDEX _tb_state_bucket ON _tb_state (bucket, collection, id);
CREATE INDEX _tb_state_dead ON _tb_state (stamp) WHERE live = 0;

CREATE TABLE _tb_peers (node INTEGER PRIMARY KEY,     -- every origin and every peer of this database
  applied INTEGER NOT NULL,   -- highest stamp applied from this origin, in stream order (or heartbeat)
  acked   INTEGER NOT NULL,   -- sender side: highest local change_id this peer acknowledged
  stable  INTEGER NOT NULL,   -- the peer's last reported stable stamp (section 11)
  seen    INTEGER NOT NULL);  -- Unix ms last heard from
CREATE TABLE _tb_feed (origin INTEGER NOT NULL, stamp INTEGER NOT NULL,
  collection TEXT NOT NULL, id TEXT NOT NULL, deleted INTEGER NOT NULL);
CREATE INDEX _tb_feed_order ON _tb_feed (origin, stamp);
CREATE TABLE _tb_feed_floor (origin INTEGER PRIMARY KEY, stamp INTEGER NOT NULL); -- index complete above this
CREATE TABLE _tb_merkle (bucket INTEGER PRIMARY KEY, h1 INTEGER NOT NULL, h2 INTEGER NOT NULL);
CREATE TABLE _tb_tail (id INTEGER PRIMARY KEY CHECK (id = 1), change_id INTEGER NOT NULL);
-- plus the unique-constraint tables (unique_keys, unique_invalid, unique_conflicts) renamed _tb_unique_*,
-- and turso_cdc, created by PRAGMA capture_data_changes_conn('after') on the writer connection.
```

- **Cheap per-field merge.** A document is one `_tb_state` row plus one collection row. A merge reads both by key,
  joins two sorted entry lists in O(fields), and writes both back. That is two captured rows, not one row per field.
- **JSON queries and indexes** read `doc`, which is always the current materialized value. Expression indexes are on
  the collection table, and tombstones never enter it, so no partial `body IS NOT NULL` index is needed. The
  `SqlSubset` compiler targets `"<collection>"` instead of `documents WHERE collection = …`.
- **Names.** Collection names match `[a-z][a-z0-9_-]{0,62}`, so they never collide with `_tb_*` or the reserved
  metadata collections. The prefixes `sqlite_` and `turso_` become reserved (409 `reserved_name`).
- **Metadata** stays as documents in the reserved collections `_collections`, `_indexes` and `_unique`. They replicate
  like any document. When one of them materializes, the lane runs local DDL: `CREATE TABLE`, `CREATE INDEX` or the
  `DROP`.
- **Dropping a collection** deletes its `_collections` document at version D. The lane drops the table and its
  `_tb_state` rows locally. Any later-arriving entry of that collection below D is dominated and dropped.
- `_system.db` uses the same layout (section 15).

## 6. Write path

1. The endpoint validates as today (JSON, size, collection schema, unique constraints on the local state). The
   request enters the database's write lane (`SqliteWriteLane` group commit). Unconditional `POST /transactions` stays
   atomic.
2. In the lane's transaction (`synchronous = NORMAL`, [0003](decisions/0003-sqlite-synchronous-normal.md)), for each
   operation:
   1. Mint `S`.
   2. Point-read `_tb_state` and the collection row.
   3. Decode, apply the operation (section 4), normalize and materialize.
   4. Write the collection row (INSERT, UPDATE or DELETE), then the `_tb_state` row with `stamp = S`,
      `origin = self` and the new digest. Both rows are always written, even when the doc bytes did not change, and
      always in this order: the tailer pairs them.
   5. Fold `old ⊕ new` digests into the lane's in-memory bucket deltas.
3. Commit, then answer. The body carries the materialized document and `version` (from `S`). ETags stay
   `tbv1-<hash of state>` for caching only. **No write-wait:** acknowledgement means one machine committed (plan:
   "ack after one node"). `IReplicationWait` goes.
4. After the commit, the lane signals the tailer and invalidates the document cache by key. This replaces
   `StampingStorageEngine`.

`If-Match`, `If-None-Match`, `assert` and `assert_count` are deleted from every route (400 `unsupported_precondition`
until the SDKs drop them). Create-only `If-None-Match: *` goes too. Client ULIDs make creates collision-free.

## 7. CDC tailer → change groups

- **Mode `after`.** Shipping, the feed and digests need no `before` image (section 2). Slice R3 checks that `after`
  records updates and deletes on 0.8.2. If it does not, the mode is `full`, which only costs bytes.
- **One tailer per open database**, driven by the lane's commit signal plus a 1 s timer:
  1. On a read connection, read CDC rows with `change_id > _tb_tail.change_id`, up to the last commit row
     (`change_type = 2`), in pages of at most 4096 rows. Decode them in the same query with
     `bin_record_json_object(table_columns_json_array(table_name), after)`. Our tables are never altered, so the
     `ALTER TABLE` caveat does not apply.
  2. Group the rows by `change_txn_id`. Within a transaction, pair each collection-row change with the `_tb_state`
     change that follows it.
  3. For each transaction, take the `_tb_state` rows whose after-image has `origin = self`. These form one **change
     group** `{origin, firstChangeId, lastChangeId, deltas[]}`. Each delta is `(collection, id, S, entries with
     changed = S, plain values from the paired doc image)`. A transaction with no local rows is not shipped.
  4. For every `_tb_state` row, local or remote, append `_tb_feed(origin, stamp, collection, id, deleted = 1 - live)`.
     Repairs (origin 0) write their own feed rows when they apply (section 14).
  5. On the write lane, in one transaction with capture turned off for that transaction (`PRAGMA … ('off')` and back),
     do three things:
     - write the feed rows and the bucket deltas;
     - advance `_tb_tail`;
     - delete CDC rows up to `min(_tb_tail, min over live peers of acked)`, always keeping the newest row.

     If toggling the pragma per transaction fails on 0.8.2, the tailer's own rows are captured too, recognized by
     table name and dropped at the next pass.
- **`turso_cdc` is the outbox.** Groups are rebuilt from it on demand for each peer (`acked < change_id`), so there is
  no second log. A peer silent for more than 24 h, or more than 1 GiB behind, stops holding CDC rows. It must refill
  when it returns (section 10).
- **Atomicity.** One Turso transaction → one group → one apply transaction at the receiver. Readers never see part of
  an operation, nor part of a lane batch.

## 8. Transport

- **Peers.** Phase 3a's `FlyMachines` directory: TXT `vms.ts-terrabase.internal`, refreshed every 5 s. A database's
  peers are the machines whose region is in its placement (section 16), minus self. They are addressed per machine at
  `http://<machine>.vm.ts-terrabase.internal:8081`: the internal listener on `fly-local-6pn` (h2c, gRPC). Region names
  like `<region>.ts-terrabase.internal` serve routing only, because a region will hold more than one machine. There is
  no mTLS and no service auth on 6PN (plan decision). `PeerCredentials` and the `peer-*` settings go.
- **Protocol** (replaces `terrabase/v1/replication.proto`, `catalog.proto`, `wan.proto` and
  `terrabase/alpha/anti_entropy.proto`; package `terrabase.v1`, everything v1):

```proto
service Replication {
  rpc Push(stream PushFrame) returns (stream AckFrame);        // one per (sender, receiver) machine pair
  rpc Digest(DigestRequest) returns (DigestReply);             // 4096 bucket digests of one database
  rpc ReadBucket(BucketRequest) returns (stream StatePage);    // states of one bucket, paged
}
message PushFrame { oneof kind { Hello hello = 1; Batch batch = 2; Heartbeat heartbeat = 3; } }
message Hello { fixed64 node = 1; fixed64 hlc = 2; repeated Resume databases = 3; }
message Resume { bytes database_id = 1; }                       // receiver answers with its acked change_id
message Batch { bytes database_id = 1; fixed64 origin = 2; uint64 last_change_id = 3;
                bytes groups = 4; }                             // LZ4 envelope (decision 0007) of encoded groups
message Heartbeat { fixed64 hlc = 1; }                          // every 1 s when idle: advances applied[origin]
message AckFrame { bytes database_id = 1; uint64 change_id = 2; fixed64 applied = 3; fixed64 stable = 4;
                   bool refill_required = 5; }
```

- **Sender.** One `Push` stream per peer machine carries every database the pair shares. It holds at most one batch
  (≤ 1024 groups, ≤ 2 MiB) in flight per database and resumes from the receiver's acked `change_id` after a
  reconnect. The `ReplicationSender` of PR #11 takes its peers once at start. The new sender follows the directory
  continuously.

## 9. Receive and merge path

1. The receiver checks the batch (a known database, origin = the stream's node, `last_change_id` advancing) and
   decodes groups. Decoding is a trust boundary with the `DeltaGroup` bounds: ≤ 1024 deltas and ≤ 2 MiB per group.
2. Groups apply in stream order on the write lane. Each batch is one transaction with `synchronous = FULL`, so an ack
   means the change is durable on the replica. For each delta:
   1. `Observe(S)`.
   2. Read the row, join, normalize and materialize.
   3. Write only if the state changed, with `stamp = S` and `origin = the delta's origin`.
   4. Run local DDL if the row is metadata.
   5. Set `applied[origin] = max(applied, last S)`.
3. After the commit, the receiver acks `(change_id, applied, stable)`.

- **Idempotent and commutative.** Applies are joins. A duplicate, a replay after reconnect or an anti-entropy page
  that overlaps a stream changes nothing. Cross-origin order does not matter. No dependency vectors are needed:
  removes carry the dots they remove, and clears carry their versions.
- **No echo loops.** Three things prevent them:
  - the shipper sends only CDC rows whose after-image has `origin = self`;
  - an applied row carries the remote origin;
  - repairs carry origin 0.

  A machine never re-sends what it received, and every origin sends to every replica directly. With 1 machine per
  region that is 1 stream per database per pair. `// anna-full`: in-region relays once regions run 3 to 12 machines.
- **Unique constraints** stay deterministic on materialized rows, unchanged in semantics (smallest id wins; losers go
  to `_tb_unique_conflicts` and stay out of the collection table). A merge never fails a constraint.

## 10. Anti-entropy and refill

- **Digests.** A document's digest is the first 128 bits of SHA-256(state without `changed`, doc bytes), kept in
  `_tb_state.h1/h2`. A bucket's digest is the XOR of its documents' digests, kept in `_tb_merkle` and updated from the
  lane's in-memory deltas at each tailer pass.
  - After an unclean start (the epoch rose), `_tb_merkle` is rebuilt by streaming `_tb_state (bucket, h1, h2)` before
    the first round.
  - The whole tree is 4096 × 16 bytes = 64 KiB, so one level suffices.
- **Rounds.** Every 60 s per (database, peer), and at every stream (re)connect:
  1. The node calls `Digest` and compares buckets.
  2. For each bucket that differs in two consecutive rounds (so in-flight streams settle), it calls `ReadBucket`
     both ways and joins the pages with `origin = 0`.
  3. A state whose entries are all below the receiver's GC horizon (section 11) and dominated, or already collected,
     is dropped instead of re-inserted. So repairs never bring back collected tombstones.
- **Refill** (a new replica, a machine back after more than 24 h, a peer told `refill_required`, a volume lost):
  1. Ship the machine's own unsent CDC groups first. These are real local writes with fresh versions, so they are
     safe to send.
  2. Delete the database file and create it empty.
  3. Copy all 4096 buckets from one up-to-date peer.
  4. Set `_tb_feed_floor[origin]` = that peer's `applied` vector at copy start, and set `applied` to it.
  5. Stream normally from there. `readyz` for that database stays false until step 3 completes.

  Refilling, not merging old state, is what keeps GC safe for a machine that was away.
- Today's `MerkleTree` (16 leaves, full scan), `AntiEntropyService`, `AntiEntropyWorker`, `AntiEntropyState` and the
  alpha proto are replaced.

## 11. Garbage collection

- **Stable stamp of a machine** = min over every origin in its directory of `applied[origin]`, where its own entry is
  its clock. Heartbeats advance idle origins. A directory machine never heard from counts as 0, so it holds GC. The
  machine reports `stable` in every ack.
- **Horizon** at machine X = (min over the database's replica machines of their last reported `stable`) − 60 s. When
  the horizon passes a stamp, every replica has applied every event at or below it, from every origin. Because each
  origin mints in commit order (section 3), no older event can still arrive.
- **Collected below the horizon**, in bounded lane batches (≤ 1024 rows) every 60 s:
  - `_tb_state` rows with `live = 0` whose newest entry is below the horizon (index `_tb_state_dead`);
  - tombstoned bases, but only when the field has no live counter entry or dot in that epoch. Removing the base would
    move the epoch back and kill them;
  - removed dots, except a removed explicit dot at `base.version` of an array base: it hides an implicit element of
    that base, so it stays as long as the base does.

  Dominated entries go at once through normalization (section 4).
- **Eviction.** A machine missing from the directory for more than 24 h leaves the horizon and the CDC retention. If it
  returns, it refills (section 10). A down region therefore holds tombstones and CDC for at most 24 h.
- **Feed retention.** `_tb_feed` rows older than 7 days are deleted and `_tb_feed_floor` advances. Change groups leave
  `turso_cdc` as in section 7.

## 12. SQL writes

`POST …/sql` accepts `INSERT`, `UPDATE` and `DELETE` in the `SqlSubset` grammar (SqlParserCS, SQLite dialect). The
same bounds apply as for reads: 16 KiB, 256 conditions, no joins, no subqueries, no functions in `WHERE`. Turso cannot
cancel a statement (section 2), so the grammar stays bounded. Write statements need the `documents:write` scope.

| Statement | Translation |
| --- | --- |
| `INSERT INTO c (_id, f1, f2) VALUES (…), (…)` | one PUT per row. `_id` is optional (a ULID is generated). An `_id` that exists locally is an error (409, local check only; concurrent inserts across regions resolve by LWW) |
| `UPDATE c SET f = <literal>, g = g + <n>, h = <field or arithmetic> WHERE <filter>` | the lane runs the compiled `SELECT id, <expressions> FROM "c" WHERE <filter> LIMIT 1025` on the writer connection, inside the write transaction. Each row becomes `base` writes for literals and computed values, and `f = f ± <integer>` becomes `inc` (a convergent increment). Nested `SET a.b = …` is a read-modify-write of `a` |
| `DELETE FROM c WHERE <filter>` | the same `SELECT id`, then one DELETE per row |

- `WHERE` sees the local state at write time. Results are eventually consistent, as for API writes.
- One statement is one operation with one stamp. It is atomic up to 1024 documents. Up to 10,000 it commits in chunks
  of 1024, each atomic with its own stamp, and the statement as a whole is not atomic. More than 10,000 rows is 400
  `sql_too_many_rows`.
- Response: `{"affected": n, "ids": [... up to 1000]}`. No `RETURNING` in v1.
- Rejected: `INSERT … SELECT`, `ON CONFLICT`, `REPLACE`, DDL, `PRAGMA`, `ATTACH`, and writes to `_tb_*` or reserved
  collections.

Execute-and-capture was considered as an alternative: run arbitrary DML in a savepoint, read its CDC rows, roll back,
and replay them as lattice operations. It would give full SQL, but it depends on unbounded statements and on
untested savepoint-plus-CDC behaviour. It is a candidate after cancellation exists in Turso.

## 13. Counters and sets in the document API

`PATCH …/documents/by-key?…` and transaction `patch` ops, `Content-Type: application/vnd.terrabase.ops+json`, as
today, extended:

```json
{"ops": [
  {"op": "inc",    "field": "failed_logins", "by": 1},
  {"op": "add",    "field": "consumed",      "value": "01JA…"},
  {"op": "remove", "field": "roles",         "value": "admin"},
  {"op": "set",    "field": "failed_logins", "value": 0}
]}
```

- `inc` (`by`: a non-zero int64) works on an absent or integer field of an existing document. `add` and `remove` work on an absent or array
  field, with JSON scalar elements of at most 1 KiB. All three take top-level fields only (400 `nested_lattice_path`).
  A field may hold at most 10,000 set elements, and the document limit applies.
- Reads are plain JSON: a counter is a number and a set is a sorted array. Queries, indexes and SQL see the same
  values.
- The response carries the materialized document after the operation, so a caller can see a local reuse at once
  (section 17).

## 14. Changes feed

`GET /v1/projects/{p}/databases/{d}/changes?cursor=<c>[&collections=a,b]`, served as SSE with the `documents:read`
scope. The per-collection `…/changes`, `/shape` and `/subscribe` routes and their stores are deleted (plan
decision).

- **Event:** `id: <cursor after this event>`, `event: change`,
  `data: {"collection", "id", "op": "upsert"|"delete", "version", "origin", "document"}`. `document` is the current
  materialized value on the serving machine. `event: cursor` is a heartbeat every 15 s with the current cursor.
  `Last-Event-ID` resumes.
- **Cursor** = an opaque base64url vector `{origin → highest stamp delivered}` over the origins in the serving
  machine's `_tb_feed`. It holds about 10 bytes per origin with LEB128 deltas. Origins whose events all aged out of
  retention drop out of the vector.
- **Valid on any machine.** Each origin's events reach every replica in stamp order, on one ordered stream (sections 3
  and 9), and the tailer indexes them in apply order. So `cursor[o] = t` means "every event of `o` up to `t` was
  delivered". Any machine can resume by emitting, for each origin, `_tb_feed` rows with `stamp > cursor[o]`, merged
  across origins by `(stamp, origin)`.
  - An origin missing from the cursor is emitted from the start of retention. This is at-least-once delivery, with
    the dedupe key `(origin, stamp, collection, id)`.
  - When `cursor[o] < _tb_feed_floor[o]` (retention, or a refill on this machine), the answer is 410 `cursor_expired`.
- **Start.** `cursor=now` returns the serving machine's current vector. A client takes it, lists or queries, then
  tails. Events that overlap the list are harmless.
- **Repairs** (origin 0) write `_tb_feed` rows in their apply transaction, one per distinct `(node, stamp)` among the
  entries they changed, under that original identity. A machine's stream from an origin has no holes: it is applied
  in order, and an ack follows a `FULL` commit. So a repair only fills events above this machine's `applied[origin]`.
  Example: an origin crashed after shipping to one peer but not another, and the second peer gets those events by
  repair. Consumers never passed those events. The only case where the stream does have a hole is a refill, and a
  refill raises the floors (section 10).

## 15. Catalog (`_system`) as a lattice database

`_system.db` (databases, saved queries, backup policies, usage, dashboard documents, management commits) uses the same
layout, write path and streams. It is placed in **every** region, so every machine holds it and serves catalog reads
and writes locally. There is no strong reason to keep a primary. The costs, all within the plan's eventual-consistency
contract:

- **Unique names** (database per project, and the like): checked locally. A concurrent duplicate across regions
  resolves deterministically through the unique-constraint loser rule (the smallest ULID wins). The loser's
  `/operations/{id}` ends `failed: name_taken`, and its database is deleted.
- **Limits** (databases per project, plan limits) are checked against local state, so they can briefly overshoot by
  one concurrent create per region.
- **Usage totals** become `inc` counters, so every machine's counts sum without forwarding (`CatalogPrimaryClient`
  goes).
- **Management commits** ([0008](decisions/0008-management-commits.md)) stay atomic (one stamp) and idempotent by id.
  `fence` and `assert` ops are evaluated locally, with no global serializability.

Deleted: `CatalogFeed`, `CatalogFollower`, `CatalogForwarder`, `CatalogPrimaryClient`, `ForwardedRequests`,
`catalog.proto`, `ISystemReplication`, `SqliteStorageTransaction.System*`, `SqliteStorageEngine.System`,
`JournalDigest`, `VersionClock`, the system schema, `PRIMARY_REGION` and `CatalogFollower`, and PR #11's "one machine
in the primary region" shortcut. Decision 0009 is superseded.

## 16. Home region and live move

- **Placement** lives in the database's catalog document: `home` (LWW region) and `regions` (an add-wins set that
  contains `home`). It is set at create (default: the region that served the create) and is GDPR-relevant: data files
  exist only in `regions`. A machine opens a database's file only when its own region is in `regions`.
- **Routing.** A data request reaching a region outside `regions` answers `fly-replay: region=<nearest region in
  regions>`, using `home` on a tie. Fly replays it. The 421 `wrong_region` middleware, `DatabaseRegions.Check`'s WAN
  remotes and `PlacementVersion` go.
- **`/v1/operations/{id}`** (new, catalog collection `operations`): `{id, kind, database, state:
  pending|running|succeeded|failed, step, progress, error, created_at, updated_at}`. It is written by the machine
  driving the operation and replicated like any document.
- **Move** (`POST …/databases/{d}/move {"region": "sjc"}` → 202 with the operation). Every step is an ordinary lattice
  write:
  1. `add regions sjc`.
  2. The sjc machines open the database and refill (section 10), reporting progress by bucket.
  3. When sjc has every bucket and `applied` within 1 s of every origin, an sjc machine writes `home = sjc`.
  4. It then writes `remove regions gru`.
  5. Gru machines stop serving the database (fly-replay to sjc). They ship their remaining CDC until sjc acks all of
     it, then delete the file.
  6. The operation ends `succeeded`.

  Writes accepted in gru at any point before step 5 are local CDC that gru ships before deleting, so none are lost.
  Backups ship from `home` (Tigris, later).

## 17. ts-auth on lattices

- **Refresh tokens.** Each token document has `uses`, an add-wins set. A refresh does `add uses <child token id>`.
  - Locally: if the returned set already had another element, it is reuse, and the token family is revoked (a plain
    write).
  - Across regions: two concurrent refreshes each see one element. After the merge the set holds two. ts-auth tails
    `GET …/changes` on its database, sees `uses` with more than 1 element and revokes the family.

  The window is about one replication delay (the plan accepts about 1 s).
- **One-time codes:** the same pattern with `uses` = a set of request ids.
- **Lockout:** `inc failed_logins 1` on each failure, `set failed_logins 0` on success (a new epoch resets it). The
  lockout decision reads the merged value.
- ts-auth's compare-and-set paths are deleted (slice R12).

## 18. Failure modes

| Failure | Effect | Handling |
| --- | --- | --- |
| Process crash mid-batch | uncommitted operations lost, not acknowledged | committed ones are in CDC and ship after restart |
| OS crash or power loss (`NORMAL`) | the last acknowledged local commits may be lost | ones already shipped come back through anti-entropy. The epoch rises, so no version is reused |
| Volume lost | the machine's databases are gone | new member number, refill from peers. Unshipped writes (< ~100 ms) are lost: "ack after one node" |
| Region offline (partition or `machine stop`) | both sides keep accepting writes | streams resume from `acked`. Anti-entropy covers gaps. More than 24 h: evicted, refill on return |
| Duplicate, reordered or replayed groups | none | joins are idempotent and commutative |
| Concurrent writes, same field | one value everywhere | the larger `(stamp, node)` wins |
| Concurrent writes, different fields | both survive | per-field entries |
| Delete vs concurrent edit | the later version wins at document level | `clear` dominates older fields; newer fields bring the document back with only themselves |
| Clock ahead > 1 s | that writer wins concurrent LWW choices | the stamp is kept, the clock advances at most 1 s, an alert fires |
| A peer far behind | CDC grows | retention cut at 24 h or 1 GiB, then `refill_required` |
| Tailer behind | replication lag, the feed lags | the lane signal and a 1 s timer; lag is a metric (`terrabase.replication.tail_lag`) |
| Corrupt or forged group | rejected | canonical decoding at the trust boundary; the stream errors and resumes from `acked` |
| Same name created in two regions | two databases briefly | the smallest ULID wins; the other operation fails `name_taken` |
| Turso aborts (single-writer bug, early-preview CDC) | the machine restarts | an unclean start bumps the epoch and rebuilds the Merkle tree; peers keep serving |
| A database deleted while a region was away | that region still has the file | the catalog delete replicates. The machine closes and deletes files whose database is deleted or not placed in its region |

## 19. Deleted code

This list is for slice owners. Each slice deletes what it replaces (greenfield: no dual paths).

- **Cross-region:** `Features/CrossRegion/*` (`WanPuller`, `WanResponder`, `WanProtocol`, `WanMetrics`, `WanUsage`,
  `RegionPeers` from 3a, `CrossRegionExtensions`), `wan.proto`, the test `RegionBus`.
- **Replication:** today's `ReplicationSender`, `ReplicationService` and `ReplicationProtocol`, plus `DeltaGroup`,
  `OriginPositions`, `IReplicationJournal`, `IReplicationWait` (write-wait), `IReplicationPressure`, `PeerCredentials`,
  `MerkleTree`, `AntiEntropy*`, `ChangeGroup` (`TBDCG001`) and `anti_entropy.proto`. They are rewritten smaller under
  the same feature folder.
- **Catalog:** see section 15.
- **Engine:**
  - `MvRegister`, `CausalContext`, `Dot`, `DocumentWrite`, `GCounter`, the old `AwSet`, and the register parts of
    `LatticeEncoding`;
  - `SqliteStorageTransaction.Registers`, `.ChangeFeed` and `.ImportHistory`;
  - the tables `documents`, `change_log*`, `shape_history`, `commit_offset`, `changes`, `journal`,
    `replication_state` and `applied`;
  - `StampingStorageEngine` and `IWriteStamps`.
- **API:** preconditions (`If-Match`, `If-None-Match`, `assert`, `assert_count`), the per-collection change feed,
  shapes, subscriptions, `ShapeHeaders` and `IShapeSubscriptionService`.
- **Docs:** `replication.md`, `cross-region.md`, `change-feeds.md`, `shape-reads.md` and `subscriptions.md` are
  replaced by this document and the feed docs. anna-core §3 to §9 are marked superseded.

## 20. Definition of done (e2e)

Run by `tests/TerraBase.E2E` (PR #6) against Fly after every deploy, pinned with `fly-prefer-region` (60 s
convergence deadline).

| Id | Scenario | PR #6 test |
| --- | --- | --- |
| E1 | write in gru is readable in sjc, same content | `Write_in_gru_is_readable_in_sjc_with_the_same_content` |
| E2 | write in sjc is readable in gru | `Write_in_sjc_is_readable_in_gru_with_the_same_content` |
| E3 | concurrent writes, same field → one identical value | `Concurrent_writes_to_the_same_field_converge…` |
| E4 | concurrent writes, different fields → both survive | `Concurrent_writes_to_different_fields_both_survive_expected_to_fail…` (flip) |
| E5 | delete in one region, edit in the other → same state | `Delete_in_one_region_and_edit_in_the_other…` |
| E6 | bulk: 1000 documents split across regions, counts and checksums match | `Bulk_1000_documents…` |
| E7 | sjc machine stopped, writes in gru, started → catches up | `FlyOutageE2ETests.Sjc_machine_stopped_then_started…` |
| E8 | concurrent `inc` in both regions → sum; `set 0` resets | new |
| E9 | concurrent `add` and `remove` of one element → add wins | new |
| E10 | changes feed: cursor taken in gru, resumed in sjc → no missing event, per-origin order | new |
| E11 | SQL `UPDATE` in gru vs PATCH of another field in sjc → both; `SET n = n + 1` in both → +2 | new |
| E12 | database created in sjc is usable from gru; same name created in both → exactly one survives | new |
| E13 | move gru → sjc under write load → no lost write, operation `succeeded` | new |
| E14 | delete → GC → recreate → no ghost fields; region back after GC → no resurrection | new |
| E15 | ts-auth refresh token reused in gru and sjc within 1 s → family revoked in both | new (auth) |

## 21. Slice plan

Ordered PRs, each at most 1,500 changed lines. Pure deletion slices are listed separately so they stay reviewable.
Model: **Opus** = Opus 5.5 (complex, correctness-critical); **Haiku** = Haiku 5.5 (mechanical against this spec).
Metrics: R3, R4a and R6 emit the replication instruments named in `ops/grafana/README.md` (Metrics); the Grafana
dashboards and alerts already query those names.

| # | Slice | Contents | ≈ lines | Depends on | Model | Turns green |
| --- | --- | --- | ---: | --- | --- | --- |
| R1 | Field lattice library | `DocumentState`/`FieldState`: join, normalize, ops, delta-at-S, materialize, canonical encoding, digest. Property tests, under the user-approved lattice exception (anna-core decision 3): ACI, reorder and duplicate convergence, resurrection rules, counter reset, add-wins, kind precedence | 1,300 | none (start now) | Opus | lattice laws (Fast) |
| R0 | E2E harness on 3a | Adapt PR #6's `Cluster.StartNodeAsync` to PR #11 identity; add E8 to E15 as expected-failure; CI job after each deploy | 700 | PR #6, PR #11 | Haiku | E1–E6 on today's engine (local) |
| R2a | Turso layout + local write and read path | Section 5 tables, bootstrap, collection DDL from `_collections`, PUT/PATCH/DELETE/transactions through `DocumentState`, point reads, cache invalidation by lane. Deletes the old client tables and registers, and the old client replication apply paths that no longer compile (multi-region replication is down until R4a) | 1,500 | 1b engine swap (Turso provider, single writer, rowid), R1 | Opus | single-region Fast suite |
| R2b | Queries, indexes, unique, schemas on collection tables | `SqlSubset` compiler, list/query, `CREATE INDEX` on `"<c>"`, recommendations, unique tables, collection drop; planner check vs the spike's index issue | 1,200 | R2a | Haiku | Fast query and index suites |
| R2c | Delete preconditions, shapes, per-collection feed | `If-Match`/`If-None-Match`/`assert*`, `ChangeFeedService`, shapes, subscriptions, `StampingStorageEngine`, docs | deletion | R2a | Haiku | — |
| R3 | CDC tailer | Capture `after` on the lane connection (retry), tailer, pairing, change groups, `_tb_feed`, Merkle folding, checkpoint and truncation keeping the newest row, metrics; measure CDC cost on a Fly machine | 1,100 | R2a | Opus | Fast: groups match writes, no echo, atomic transactions |
| R4a | Peer streams | New proto, sender per peer from CDC with retention by acks, receiver join path (`FULL`), HLC observe, `origin`, heartbeats, `applied`/`stable`, placement-based peers from `FlyMachines` | 1,500 | R3, 3a (#11 + 8081 internal listener) | Opus | E1–E6 (local harness) |
| R10a | Catalog as lattice DB | `_system` on the new layout, placed everywhere, unique-name loser + `name_taken`, usage counters, management commits local | 1,400 | R4a | Opus | E1–E6 on Fly, E12 |
| R4b | Delete WAN, catalog primary, old replication | `CrossRegion/*`, `Catalog*`, `ForwardedRequests`, old protos, `PeerCredentials`, write-wait, `JournalDigest`, `VersionClock`, 0009/0010 superseded | deletion | R10a | Haiku | — |
| R5 | Counters and sets API | `inc`/`add`/`remove` ops, validation, OpenAPI, docs | 600 | R2a (R4a for e2e) | Haiku | E8, E9 |
| R6 | Anti-entropy + refill | `Digest`/`ReadBucket`, rounds, Merkle rebuild after unclean start, refill, `refill_required`, CDC retention cut, readiness per database | 1,300 | R4a | Opus | E7 |
| R8 | Changes feed SSE | `/databases/{id}/changes`, vector cursor, `cursor=now`, `Last-Event-ID`, collection filter, 410, retention | 900 | R3 (R4a for e2e) | Haiku | E10 |
| R9 | SQL writes | `INSERT`/`UPDATE`/`DELETE` grammar, compiled `SELECT` in the write transaction, `inc` detection, chunking, caps, scope | 1,200 | R2b, R5 | Opus | E11 |
| R7 | GC | `stable`/horizon, directory-based origins, eviction after 24 h, tombstone and removed-dot collection, feed retention | 800 | R6 | Opus | E14 |
| R11 | Home region + live move | `home`/`regions`, file placement, `fly-replay`, `/v1/operations/{id}`, move steps, file drop after acks; delete 421 placement | 1,400 | R10a, R6 | Opus | E13 |
| R12 | ts-auth lattice-native | `uses` sets on refresh tokens and codes, lockout counter, `/changes` watcher revoking families; delete ts-auth CAS | 1,200 | R5, R8 | Opus | E15 |

**R2a as built** (2026-10-12), in three PRs:

1. R2a-1 deletes the client register replication (sender, receiver, anti-entropy, write-wait, journal, applied
   positions) and also the CrossRegion WAN pull, because it applied register groups and no longer compiled.
2. R2a-2 stores every client document as a `DocumentState` in `_tb_state` (stored form, base64url, bucket, digest,
   `origin`, `live`) and deletes `MvRegister`, `CausalContext`, `Dot`, `DocumentWrite` and `LatticeEncoding`. PATCH
   writes only the top-level fields it changes (`IStorageTransaction.PatchAsync`). A request mints one stamp per
   document, and its `_tb_state` row is written once, at the end of the request (group commit: before the write's
   savepoint is released; otherwise at commit). The materialized row may be rewritten by each operation of the
   request; the tailer (R3) pairs a state row with the last document row before it. A collection drop deletes its
   rows and states locally and deletes its `_collections` document. Materialized rows still live in `documents`.
3. R2a-3 moves the materialized rows to one rowid table per collection (`id TEXT NOT NULL UNIQUE, doc TEXT NOT
   NULL`), together with the document and `SqlSubset` compilers, indexes and unique tables on them (the core of R2b),
   because the Fast suite cannot stay green with queries on a table that no longer holds the rows. Versions are read
   from `_tb_state` by a correlated lookup; a document a request wrote reads its version from memory until the request
   ends. Index keys end with `id` (the tie-break every query sorts by); an index cannot key `_rev`. Until R2b and R2c
   rename or delete them, the old engine tables (`collections`, `commit_offset`, `shape_history`, `change_log*`,
   `unique_*`) are reserved collection names, as are the `sqlite_` and `turso_` prefixes (400 `reserved_name`). The
   query compiler still emits `json_type` and `json_each`; R2b makes it `json_extract`-only and adds the `IN` and
   prefix-`LIKE` rewrites and the planner check.

R2a is built on the Microsoft.Data.Sqlite engine and writes only SQL that Turso accepts (rowid tables for every new
table, plain `INSERT`/`UPDATE`/`DELETE` on captured tables). The engine swap (1b, PR #21) lands separately: on Turso,
174 Fast tests fail, most of them in continuous backup, which reads SQLite's WAL directly.

**Order and parallelism:**

1. R1 ∥ R0, starting now.
2. R2a, once 1b lands.
3. R2b ∥ R2c ∥ R3.
4. R4a, once 3a lands.
5. R10a.
6. R4b ∥ R5 ∥ R6 ∥ R8.
7. R9 ∥ R7 ∥ R11.
8. R12.

The critical path is R1 → R2a → R3 → R4a → R10a → R6 → R11. Opus 9 slices, Haiku 7.

**Replication outage window.** Between R2a and R10a, main does not replicate across machines. This is acceptable:
the Fly apps hold no customer data yet and nothing replicates on Fly today (no peer configuration), but it must be a
known state. The e2e job marks E1 to E7 expected-failure in that window.

## 22. Open questions and risks

1. **CDC cost on Fly.** The only valid measurement is −37 % in WAL. If R3 measures a lane below about 500 operations/s
   on `shared-cpu-2x`, the fallbacks are:
   - capture only `_tb_state`, by writing collection rows with capture off, if per-statement toggling works;
   - batch more operations per lane transaction.
2. **`BEGIN CONCURRENT`** (MVCC + CDC on Turso main). Concurrent commits break "commit order = stamp order" per
   origin, which the feed cursor and GC rely on. Before adopting it, either mint stamps in a pre-commit hook, or move
   cursors and horizons to per-origin commit sequences. Revisit when Turso calls MVCC production-ready.
3. **`after` mode and pragma toggling** are untested on 0.8.2. R3 verifies both and falls back as described in
   section 7.
4. **3a's WAN-over-gRPC** (`refactor/fly-wan-grpc`) is interim: R4b deletes it. If it is more than about a day from
   merging, landing only #11 plus the 8081 internal listener is enough for R4a.
5. **Nested fields** are LWW per top-level key in v1 (section 4).
6. **Spike report fixes** (PR #12: the invalid CDC and encryption figures, the BackupDatabase GO that contradicts the
   recorded FAIL) should land before R3 relies on its numbers.
