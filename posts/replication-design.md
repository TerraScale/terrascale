# Replication design

> DRAFT — launch post outline + draft paragraphs. Not published.

## Outline

- The shape (D-35): leaderless active-active across regions — every region has its own writer per database; no home region, no single-writer mode.
- Inside a region: the second copy is a physical WAL-frame follower of the same SQLite file; `RegionalDurable` acks when the follower fsyncs the frames; byte-identical, no logical replay; promotion is opening the file.
- Across regions: a journal tailer ships committed rows in `seq` order over pooled QUIC lanes, zstd waves with a trained dictionary.
- Causal+ delivery (COPS, D-37): a row applies only when every row in its causal context is applied; dominated rows drop; overflow triggers reconciliation, never a skip.
- Conflicts (D-36): document-level CRDT; concurrent conflicts parse both bodies, merge per field with remove-wins semantics and HLC tiebreak; losers go to history with a deterministic explanation; merge is a pure function of the observed set.
- Anti-entropy (D-45): range-based set reconciliation over `(origin, seq) → hash`; O(log n) rounds worst case; no per-connection state.
- `GlobalDurable` (D-37): acks only when every selected region fsynced the row and all causal dependencies; fails closed under 2 reachable regions.
- Reads (Occult-style session token = causal frontier): read-your-writes + monotonic by default, `eventual` or `writes-follow-reads` per request, never linearizable.
- The honest limits (D-37/D-16): unique indexes and conditional writes are enforced within a region; cross-region uniqueness is best-effort, conflicts surface in history; a replicated write costs one write per region.

## Draft

TerraScale's replication is leaderless active-active (D-35). Every region hosting a database has its own writer for it; there is no home region, no leader lease, no failover ceremony when a region goes quiet — the other regions keep accepting writes because they never stopped. That is the property multi-region customers actually want: local write latency everywhere, and no re-election cliff.

Inside a region, the second copy is as dumb as we could make it (D-35): a physical WAL-frame follower of the writer's SQLite file. The follower receives the database's own WAL frames; an ack means it fsynced them. There is no logical replay, no conflict logic, no schema awareness — the follower's file is byte-identical to the leader's. `RegionalDurable` means exactly "the second copy in this region has your frames on stable storage," and promotion of a follower is opening the file. Durability here is boring on purpose.

Cross-region is where it gets interesting. A journal tailer per peer region ships committed rows in sequence order over pooled QUIC lanes, compressed in zstd waves with a dictionary trained on canonical JSON. Delivery is causal+ (D-37): a row is applied only once every row in its causal context has been applied. Rows that arrive early — before their dependencies — wait in a bounded queue. Rows already dominated by something newer drop. If the queue overflows, we don't skip anything; we trigger reconciliation. The order-independence is the deep property (D-36): merge is a pure function of the observed set of rows, so any delivery order, duplication included, converges to identical bytes in every region.

True conflicts — concurrent writes to the same document in different regions — are merged at the document level (D-36). Both bodies are parsed, fields are merged with remove-wins map semantics, and a hybrid logical clock breaks ties. The loser doesn't vanish: both inputs and the rule that decided the outcome go to history, with a deterministic explanation you can read in the dashboard's conflict viewer — "why this version won" is a query, not a mystery. Collections that need richer semantics can declare a merge schema with typed CRDT fields (counters, sets, MV registers).

Anti-entropy repairs whatever the tailer misses — reconnects, bootstrap, queue overflow, slow drift (D-45). It is range-based set reconciliation over `(origin, seq) → row hash`, with additive range fingerprints maintained in the write transaction. Worst case it converges in O(log n) rounds, it holds no per-connection state, and a region joining the cluster bootstraps from a backup snapshot and then proves equality with one full reconciliation before it serves a single request.

Two honesty clauses close the design. First, consistency: `GlobalDurable` acks a write only when every selected region has fsynced it and all of its causal dependencies, and it fails closed below 2 reachable regions — it never guesses (D-37). Client reads carry a session token that is the client's causal frontier: read-your-writes and monotonic reads by default, `eventual` or `writes-follow-reads` per request, and never linearizable — we don't pretend to be a global serial database. Unique indexes and conditional writes are enforced within a region; cross-region uniqueness is best-effort, and the conflicts surface in history rather than being silently resolved. Second, pricing: replicating a write to another region costs one write per region, and we say so on the pricing page rather than folding it into a "global" adjective (D-16).
