# 5. Storage layout (one Turso file per database)

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
  h1 INTEGER NOT NULL, h2 INTEGER NOT NULL,   -- 128-bit digest of (collection, id, state without `changed`, doc)
  UNIQUE (collection, id));
CREATE INDEX _tb_state_bucket ON _tb_state (bucket, collection, id);
CREATE INDEX _tb_state_dead ON _tb_state (stamp) WHERE live = 0;

CREATE TABLE _tb_peers (node INTEGER PRIMARY KEY,     -- every origin and every peer of this database
  applied INTEGER NOT NULL,   -- highest stamp applied from this origin, in stream order (or heartbeat)
  acked   INTEGER NOT NULL,   -- sender side: highest local change_id this peer acknowledged
  incarnation TEXT NOT NULL DEFAULT '', -- the local file incarnation whose change_id the ACK names; empty before ACK contact
  stable  INTEGER NOT NULL,   -- the peer's last reported stable stamp (section 11)
  seen    INTEGER NOT NULL);  -- Unix ms last heard from
CREATE TABLE _tb_feed (seq INTEGER NOT NULL UNIQUE, commit_offset INTEGER NOT NULL,
  origin INTEGER NOT NULL, stamp INTEGER NOT NULL,
  collection TEXT NOT NULL, id TEXT NOT NULL, deleted INTEGER NOT NULL, prefix BLOB NOT NULL);
CREATE INDEX _tb_feed_order ON _tb_feed (origin, stamp);
CREATE TABLE _tb_feed_sequence (id INTEGER PRIMARY KEY CHECK (id = 1), seq INTEGER NOT NULL);
INSERT INTO _tb_feed_sequence (id, seq) VALUES (1, 0);
CREATE TABLE _tb_feed_floor (origin INTEGER PRIMARY KEY, stamp INTEGER NOT NULL, prefix BLOB NOT NULL); -- index complete above this
CREATE TABLE _tb_merkle (bucket INTEGER PRIMARY KEY, h1 INTEGER NOT NULL, h2 INTEGER NOT NULL);
CREATE TABLE _tb_tail (id INTEGER PRIMARY KEY CHECK (id = 1), change_id INTEGER NOT NULL);
-- plus the unique-constraint tables _tb_unique_keys, _tb_unique_invalid and _tb_unique_conflicts (rowid, UNIQUE keys),
-- and turso_cdc, created by PRAGMA capture_data_changes_conn('after') on the writer connection.
```

The [shape storage foundation](../shape-storage.md) adds a persisted local feed sequence
and the derived `_tb_shape_chunk` / `_tb_shape_snapshot` tables. Shape bytes are
written with capture suspended and are omitted from database copies and backups.

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
- **Dropping a collection** deletes its `_collections` document at version D. Local drops and joined drops apply
  that deleted clear to every document state using the document lattice, including hidden uniqueness losers. Entries
  dominated by D disappear; newer live bases, set adds and tombstones remain. Compare entry versions by HLC **and
  node**, never by `_tb_state.stamp` (the last applied operation can be older than the entries the row holds).
  A state reduced to the collection's clear alone needs no separate document tombstone: the `_collections` tombstone
  covers it. If no document state remains, the table and collection row go. Otherwise the collection stays, including
  when only newer document tombstones remain, so a delayed older PUT cannot resurrect them.
  Every later-arriving document delta joins with the collection's retained deleted clear, even while the table exists;
  a newer field write brings back only fields not covered by D. Without live collection metadata above D, a retained
  or implicitly resurrected collection has the same derived ULID on every replica (the big-endian packed HLC and node
  of D), with `created_at` equal to D's physical milliseconds. Explicit recreation uses its replicated identity and
  creation-time fields instead. The derived identity is a local projection, not a new metadata write. Collection
  creation and metadata replacement write fields (and field tombstones for omitted properties), reserving the clear
  register for drops. Recreation writes new metadata fields above D while retaining the deleted clear, so it does not
  erase the barrier against old document deltas. Physical indexes with definitions covered by D go; definitions newer
  than D are retained or rebuilt when the collection exists. An index definition alone does not recreate a dropped collection.
  Unique definitions covered by D are inactive, including definitions received after the drop. Local document and
  definition creations mint above the retained clear even when observation of a future peer clock was capped.
  Unique visibility is reconciled as covered documents leave. Rewritten survivor states use origin 0, like a repair, so the CDC tailer
  does not echo the collection's deletion as document writes. The wire and storage encodings are unchanged; every
  serving replica must use field-only collection metadata writes to retain this deletion barrier. Drops scan states
  in pages of 128 IDs within one atomic transaction; large collections hold the write lock until the scan finishes.
  Derived projections do not count toward outbound change-group limits because only the metadata drop ships.
- `_system.db` replicates catalog document states through CDC (section 15), with its materialized documents in one
  shared table. It has no client changes-feed tables; catalog writes do not append client feed events.
