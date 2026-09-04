# Why TerraScale runs on SQLite

> DRAFT — launch post outline + draft paragraphs. Not published.

## Outline

- The choice: mainline SQLite via rusqlite, exact version pinned, amalgamation build (D-27) — not libSQL, not a custom engine.
- One SQLite file per customer database: WAL mode, one writer plus a read pool, group commit (D-42/D-43).
- The publication journal is folded into the documents table: history is journal retention, previous rows stay reachable until the history window passes (D-42).
- Bytes everywhere (D-30): parse once with simd-json at write time, canonicalize, extract indexed fields — then the body is opaque canonical JSON bytes on every boundary.
- Zero-parse reads: responses splice body bytes; `serde_json::Value` never appears on a data path.
- Auto-indexing with zero stale reads: index maintenance happens in the write's own transaction, so a visible index can never serve a stale read (D-31).
- What this buys for free (D-39): database branching, PITR via Litestream, full-text search (FTS5), read-only SQL over HTTP.
- The honest limits: transactions are single-database (one file); cross-database transactions never exist.

## Draft

Every database on TerraScale is one mainline SQLite file. Not a fork, not libSQL, not an LSM engine we wrote ourselves: upstream SQLite via rusqlite, with the exact version pinned and built from the amalgamation (D-27). We chose it for the least glamorous reason available: it is the most battle-tested storage engine on earth. SQLite has spent decades being embedded in every phone, browser, and embedded device on the planet, and its failure modes are documented exhaustively. When your product is durability, you buy the engine whose bugs have already been found by other people.

The operational shape follows from the file. Each database runs in WAL mode with one writer and a read pool; a dedicated committer thread batches writes into fixed 1–2 ms group-commit waves (D-43). There is no lock manager, no background compaction subsystem, no page-cache tuning folklore. Hot databases that outgrow a single writer split by key range into more files with journal catch-up and an atomic cutover — never two writers on one file, never intra-region multi-master (D-28).

We folded the publication journal into the documents table itself (D-42). Every write commits the document row, its causal metadata, and its extracted index values in one SQLite transaction. That single decision collapses a whole layer of machinery: history is not a separate subsystem, it is journal retention — previous versions of a row stay reachable by `(doc_id, seq)` until the collection's history window passes. Idempotency is a unique partial index on the client request ID. Usage metering is counters flushed per commit wave and reconciled from the same journal on restart. One B-tree write per operation, one source of truth.

The second pillar is bytes everywhere (D-30). A document is parsed exactly once, at write time, with simd-json: validate, canonicalize, extract the fields your schema indexes. After that the body is opaque canonical JSON bytes — a BLOB in SQLite, `bytes` on every internal boundary, spliced raw into HTTP responses, shipped as-is in replication frames and inside reactive deltas. Nothing downstream parses your document to serve it. Reads are zero-parse by construction; the public consequence is that reads return semantically, not byte-, identical JSON.

Indexing is where most databases make you choose between performance and correctness. We don't (D-31): auto-indexing tracks your query templates, plans candidates against hypothetical indexes, and builds the winners invisibly under a write budget — but the invariant that matters is that an index is maintained in the write's own transaction. A visible index can never serve a stale read, because the index and the data move in the same commit. No async index builds you have to reason about, no eventually-consistent secondary indexes.

Finally, the compounding payoff (D-39): because a database is a file, branching is a copy-on-write fork of the file, PITR is Litestream streaming WAL frames to object storage, full-text search is SQLite's FTS5, and ad-hoc analysis is read-only SQL over HTTP. None of these required us to build a feature; they required us to not build an engine that made them hard. The honest footnote: transactions are single-database. One file, one transaction — cross-database transactions never exist, by design.
