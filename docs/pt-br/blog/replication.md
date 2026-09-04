---
---
<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

# Replication design (draft)

> DRAFT. This launch post is not published. It describes the launch design;
> convergence and durability measurements are part of the launch gates.

## Outline

- The shape (D-35): leaderless active-active across regions. Every region has its own writer per database; there is no home region or single-writer mode.
- Inside a region: the second copy is a physical WAL-frame follower of the same SQLite file; `RegionalDurable` acks when the follower fsyncs the frames; promotion is opening the file.
- Across regions: a journal tailer ships committed rows in `seq` order over pooled QUIC lanes, with zstd waves and a trained dictionary.
- Causal+ delivery (COPS, D-37): a row applies only when every row in its causal context is applied; dominated rows drop; overflow triggers reconciliation, never a skip.
- Conflicts (D-36): document-level CRDT; concurrent conflicts parse both bodies, merge per field with remove-wins semantics and HLC tiebreak; losers go to history with a deterministic explanation; merge is a pure function of the observed set.
- Anti-entropy (D-45): range-based set reconciliation over `(origin, seq) → hash`; the design targets O(log n) rounds in the worst case and holds no per-connection state.
- `GlobalDurable` (D-37): acks only when every selected region fsynced the row and all causal dependencies; fails closed under 2 reachable regions.
- Reads (Occult-style session token = causal frontier): read-your-writes and monotonic reads by default, `eventual` or `writes-follow-reads` per request, never linearizable.
- The honest limits (D-37/D-16): unique indexes and conditional writes are enforced within a region; cross-region uniqueness is best-effort; conflicts surface in history; a replicated write costs one write per region.

## Draft

TerraScale's replication is leaderless active-active (D-35). Every region hosting a database has its own writer for it. There is no home region or leader lease. If a region becomes unavailable, the other regions can continue accepting writes because they do not depend on a re-election. The launch tests still have to measure the resulting availability and write behavior.

Inside a region, the second copy is a physical WAL-frame follower of the writer's SQLite file (D-35). The follower receives the database's WAL frames, and an ack means it fsynced them. There is no logical replay, conflict logic, or schema awareness. The follower's file is byte-identical to the writer's. `RegionalDurable` means exactly "the second copy in this region has your frames on stable storage." Promotion of a follower opens that file. Durability here is boring on purpose.

Cross-region delivery uses a journal tailer per peer region. It ships committed rows in sequence order over pooled QUIC lanes, compressed in zstd waves with a dictionary trained on canonical JSON. Delivery is causal+ (D-37). A row applies only after every row in its causal context has applied. Rows that arrive early wait in a bounded queue. Rows already dominated by something newer drop. If the queue overflows, the receiver triggers reconciliation instead of skipping anything. Merge is a pure function of the observed set of rows (D-36), so delivery order and duplication do not change the converged bytes. The launch simulation and three-region gate measure this behavior under faults.

True conflicts, concurrent writes to the same document in different regions, are merged at the document level (D-36). Both bodies are parsed. Fields are merged with remove-wins map semantics, and a hybrid logical clock breaks ties. The loser does not vanish. Both inputs and the rule that decided the outcome go to history, with a deterministic explanation that the dashboard conflict viewer can show. Collections that need richer semantics can declare a merge schema with typed CRDT fields such as counters, sets, and MV registers.

Anti-entropy repairs whatever the tailer misses, including reconnects, bootstrap, queue overflow, and slow drift (D-45). It is range-based set reconciliation over `(origin, seq) → row hash`, with additive range fingerprints maintained in the write transaction. The design targets O(log n) rounds in the worst case and holds no per-connection state. A joining region bootstraps from a backup snapshot, then runs a full reconciliation before it serves requests. The launch gate must verify these steps.

Two honesty clauses close the design. First, consistency: `GlobalDurable` acks a write only when every selected region has fsynced it and all of its causal dependencies. It fails closed below 2 reachable regions. Client reads carry a session token that is the client's causal frontier, providing read-your-writes and monotonic reads by default. Clients may request `eventual` or `writes-follow-reads`; reads are never linearizable. Unique indexes and conditional writes are enforced within a region. Cross-region uniqueness is best-effort, and conflicts surface in history rather than being silently resolved (D-37). Second, pricing: replicating a write to another region costs one write per region, and the pricing page states that directly (D-16).
