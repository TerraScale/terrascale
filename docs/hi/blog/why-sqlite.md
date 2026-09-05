---
---
<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

# Why TerraScale runs on SQLite (draft)

> DRAFT. This launch post is not published. The storage decision is fixed;
> publication waits for the benchmark record described below.

## Outline

- The choice: mainline SQLite via rusqlite, with the exact version pinned and the amalgamation build (D-27).
- One SQLite file per customer database: WAL mode, one writer plus a read pool, group commit (D-42/D-43).
- The publication journal is folded into the documents table: history is journal retention, previous rows stay reachable until the history window passes (D-42).
- Bytes everywhere (D-30): parse once with simd-json at write time, canonicalize, extract indexed fields, then keep the body as opaque canonical JSON bytes on every boundary.
- Zero-parse reads: responses splice body bytes; `serde_json::Value` never appears on a data path.
- Auto-indexing with zero stale reads: index maintenance happens in the write's own transaction, so a visible index cannot serve a stale read (D-31).
- What this buys for launch (D-39): database branching, PITR via native Rust backups, full-text search (FTS5), and read-only SQL over HTTP.
- The honest limits: transactions are single-database (one file); cross-database transactions never exist.

## Draft

Every database on TerraScale is one mainline SQLite file. We use upstream SQLite through rusqlite, with the exact version pinned and built from the amalgamation (D-27). We chose it for the least glamorous reason available: it is the most battle-tested storage engine on earth. SQLite has spent decades embedded in phones, browsers, and other devices, and its failure modes are well documented. For a durability product, using an engine that has already been exercised this hard is a sensible starting point.

The operational shape follows from the file. Each database runs in WAL mode with one writer and a read pool. A dedicated committer thread batches writes into fixed 1–2 ms group-commit waves (D-43). We do not add a separate compaction subsystem or storage format. Hot databases that outgrow a single writer split by key range into more files with journal catch-up and an atomic cutover. There are never two writers on one file or intra-region multi-master (D-28).

The publication journal is folded into the documents table itself (D-42). Every write commits the document row, its causal metadata, and its extracted index values in one SQLite transaction. That decision removes a separate journal store: history is journal retention, so previous versions of a row stay reachable by `(doc_id, seq)` until the collection's history window passes. Idempotency is a unique partial index on the client request ID. Usage metering is counters flushed per commit wave and reconciled from the same journal on restart. One B-tree write per operation, one source of truth.

The second pillar is bytes everywhere (D-30). A document is parsed exactly once at write time with simd-json. The write path validates it, canonicalizes it, and extracts the fields your schema indexes. After that, the body is opaque canonical JSON bytes. It is a BLOB in SQLite, `bytes` on every internal boundary, spliced into HTTP responses, shipped as-is in replication frames, and carried inside reactive deltas. Nothing downstream parses the document to serve it. Reads are zero-parse by construction. The public consequence is that reads return semantically, not byte-, identical JSON.

Indexing is where databases often make you choose between performance and correctness. TerraScale's auto-indexing tracks query templates, plans candidates against hypothetical indexes, and builds selected indexes invisibly under a write budget (D-31). The invariant that matters is that an index is maintained in the write's own transaction. A visible index cannot serve a stale read, because the index and the data move in the same commit. Index-build progress can affect when an index becomes visible, but it does not create an eventually consistent read path.

Finally, the practical payoff (D-39): because a database is a file, a branch can start from a database snapshot, PITR will use TerraBase’s internal Rust backup subsystem to archive committed SQLite changes to object storage, full-text search uses SQLite's FTS5, and ad-hoc analysis uses read-only SQL over HTTP. These launch features build on the same storage model. The honest footnote is that transactions are single-database. One file, one transaction. Cross-database transactions do not exist by design.

The backup subsystem will rebuild the required functionality of
[Litestream](https://github.com/benbjohnson/litestream) natively inside
TerraBase, informed by a review of its source. The design shares background
resources across databases and coordinates WAL capture with the engine.
Faster backup and restore and lower resource use are implementation goals
that must be demonstrated by benchmarks and recovery tests.
