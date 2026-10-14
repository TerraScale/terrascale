# 21. Slice plan: R10a as built

**R10a as built** (2026-10-12), in stacked PRs:

1. R10a-1 deletes the catalog primary, its followers and the forwarding (decision 0009): every machine writes its own
   `_system.db`. Usage and query shapes flush into the local catalog; `PRIMARY_REGION` is no longer read. Format unchanged.
2. R10a-2 makes `_system` a lattice database (format 4):
   - **Layout.** The client layout's `_tb_state`, `_tb_peers` and engine tables, but the live documents of every
     collection share one `documents (collection, id, body)` table: catalog collections are paths
     (`projects/<id>/databases`), a set per tenant, so a table per collection would grow the schema with every tenant.
     The tailer pairs a `_tb_state` row with the last `documents` row of the same (collection, id) before it.
   - **Writes** go through `DocumentState` like a client database's: HLC stamps are the versions (ETags scoped to the one
     `_system`, the same on every machine), PUT, PATCH and DELETE are lattice operations, and every deletion is a
     tombstone, so a deleted id stays taken (`GetVersionAsync` answers it deleted). The catalog also stores scalars
     (name indexes, sequences): a scalar is stored as the object `{"\u0000": value}` and read back as the value.
   - **Group bounds.** A management operation may change more documents than a client transaction (an organization's
     deletion, a commit of 1024 operations): a `_system` group holds up to 16,384 deltas and 3 MiB, inside one 4 MiB batch.
   - **Streams.** The system database is one more database of the CDC source, the sink, the peer streams and
     anti-entropy, named on the wire by the reserved ids `_system` (no client database has them: client ids are ULIDs)
     and placed in every region: a receiver never refuses it. Its catalog paths and ids are checked by size only.
   - **Prefix deletes** (`DeleteCollectionsAsync`, a deleted database's or project's state) purge this machine's rows and
     states without tombstones; no live catalog document reaches that state any more, and CDC ships no deletes.
   - Not yet: deletion and name races across regions, usage counters, per-region files of a database created elsewhere
     (R10a-3).
