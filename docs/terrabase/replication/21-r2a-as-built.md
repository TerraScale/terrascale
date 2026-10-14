# 21. Slice plan: R2a as built

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
   ends. Index keys end with `id` (the tie-break every query sorts by); an index cannot key `_rev`. Until R2c
   deletes them, the old engine tables (`collections`, `commit_offset`, `shape_history`, `change_log*`) are reserved
   collection names, as are the `sqlite_` and `turso_` prefixes (400 `reserved_name`).
