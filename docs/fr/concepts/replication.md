---
---
<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

# Replication model

TerraScale replication is **leaderless active-active**. There is no home
region, no single-writer mode, and no failover ceremony: every selected region
has its own writer per database, and all copies converge.

## Inside a region: RF=2, physical bytes

The second copy in a region is a **physical WAL-frame follower** of the
writer's SQLite file. The follower applies the writer's WAL frames verbatim —
byte-identical, no logical replay, no CRDT machinery. A write acknowledged
**`RegionalDurable`** is one whose WAL frames the follower has fsynced. If the
writer dies, promotion is opening the file under a new replica epoch.

## Across regions: causal+ delivery

A journal tailer per peer region ships committed journal rows (canonical body
bytes plus causal metadata) over pooled QUIC lanes, compressed with a
dictionary trained on canonical JSON. The receiver applies a remote row only
once **every row in its causal context is already applied** — causal+
consistency (the COPS model). Rows waiting for dependencies sit in a bounded
queue; overflow triggers reconciliation, never a skip.

Three outcomes per incoming row:

- **Dominated** (a later version of the document exists) → dropped.
- **Clean** (no concurrent version) → written as bytes, never parsed.
- **Concurrent** → both bodies are parsed and merged per field:
  remove-wins map semantics, HLC tiebreak. The merged bytes, both inputs, and
  the rule applied go to history.

Merge is a pure function of the observed set of rows, so delivery order and
duplication can never produce divergence.

## Durability classes

- **`RegionalDurable`** — the row is durable in its region (RF=2 follower
  fsync).
- **`GlobalDurable`** — every selected region has fsynced the row **and** all
  of its causal dependencies are already uniform. Fewer than two reachable
  regions fails closed: the write is refused, not weakened.

## Consistency: what you get, stated honestly

The client's **session token is its causal frontier**. Defaults:

- **Read-your-writes** and **monotonic reads** always.
- Per-request opt-outs: `eventual`, or `writes-follow-reads`.
- **Never linearizable.** If you need a linearizable read, that is not this
  database, and we say so.

A member that cannot serve your session's frontier within a bounded wait
returns `StaleReplica` naming a region that can; the SDK follows it.

Unique indexes and conditional writes are enforced **within a region**.
Cross-region uniqueness is best-effort, and any conflict is surfaced in
history with a deterministic explanation — not silently resolved.

## Anti-entropy

Range-based set reconciliation over `(origin, seq) → row hash` runs on
reconnect, region bootstrap, queue overflow, and a slow sweep — O(log n)
rounds worst case, no per-connection state. A joining region bootstraps from a
backup snapshot in its own region's object storage, then tails, then proves
equality with one full reconciliation before it serves.

## What it costs

Replicating a write to another selected region costs **one write per region**.
That is the whole rule — shown on the [pricing page](/fr/pricing/) up front,
never folded into a vague "multi-region surcharge".
