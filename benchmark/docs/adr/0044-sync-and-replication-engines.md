# ADR 0044: The two engines — replication and sync, on a research base

Status: accepted (owner decisions D-35..D-38, D-45, D-46, D-78, 2026-09-03).
Authority: the workspace `GOAL.md` owns these decisions; this ADR is the
detailed design and names the papers each mechanism is built on. If they
diverge, the workspace goal wins. Supersedes the anti-entropy and
session-token portions of ADR 0041 and the legacy query portions of ADR 0022.

## 0. Why these two engines are the product

TerraScale sells two things nobody gets from a single-region database alone: **writes in any
Region converge everywhere without a leader** (replication engine) and
**readers see query results update live at zero marginal cost per reader**
(sync engine). Everything else is table stakes. Both engines sit on the same
durable primitive: the **publication journal**, folded into the documents
table (D-42): every committed row carries `seq` (per-Region total order),
`origin` (Region), `commit_ulid`, `dot`, `context`, `hlc`, `kind`, and the
canonical body bytes.

## 1. Research base

| Mechanism | Paper / system | What we take from it |
| --- | --- | --- |
| Consistency model across Regions | COPS, Lloyd et al., SOSP 2011 ("causal+") | Causal consistency with convergent conflict handling is the strongest model available without coordination; dependency checking before exposing remote writes. |
| Client-side causality / read-your-writes | Occult, Mehdi et al., NSDI 2017; Tunable Causal Consistency, Ma et al., 2022 | Clients carry a causal timestamp; servers check they can serve it and otherwise wait or redirect; session guarantees are selectable per operation. |
| Mixing durable classes | UNISTORE, Bravo et al., ATC 2021 | A "strong" write must only depend on *uniform* (durably replicated) causal dependencies, or a Region loss can make it unreachable. Shapes `GlobalDurable`. |
| Document CRDT | Delta-state CRDTs, Almeida/Shoker/Baquero 2018; Melda, Brocco 2022/2025/2026 (delta-state JSON CRDT with deterministic reconstruction) | Ship deltas, not state; merge is a deterministic function of the set of observed updates, so replay order never matters. |
| Text fields (post-launch) | Eg-walker, Gentle & Kleppmann, EuroSys 2025 | Event-graph replay gives CRDT convergence with OT-sized steady state; the right shape for a `text` merge-schema field. |
| Anti-entropy | Range-Based Set Reconciliation, Meyer 2023; RBSR via range-summarizable order-statistics stores, 2026; Negentropy wire protocol | Fingerprint ranges, refine only mismatches: O(log n) rounds worst case, no per-connection state, no rigid tree. |
| Incremental view maintenance | DBSP, Budiu et al., VLDB 2023 / VLDB Journal 2025 | Views as streams of Z-sets; any query plan incrementalizes operator by operator; linear operators need no state, bilinear ones (joins) and aggregates need integrated state. Decides the launch live subset. |
| Ordered windows | Efficient maintenance of materialized top-k views, Yi et al., ICDE 2003 | Keep a k' > k buffer with a boundary so `ORDER BY … LIMIT` rarely requires a requery. |
| Partial replication guarantees | IntersectionAtomicity / IntersectionCC, 2026 | A subscriber that sees a subset of the data gets causal and atomic guarantees only on the intersection of interest sets; our views promise consistent cuts per view, not across views. |

Not adopted, and why: tree-based search / prolly trees (worst case degrades to
full transfer under adversarial keys; rigid shape); operation-based CRDT
replay for whole documents (state and load cost, the exact problem Eg-walker
documents); OT (server-serialized, wrong for leaderless); per-field causal
kernels on every document (metadata cost; only merge-schema collections pay
it); embedding a general IVM engine (Feldera/DBSP runtime, Materialize,
Noria): the live subset needs three operators, written by hand; Convex-style
read-set re-execution as the primary strategy (cost scales with views ×
writes; kept only as the fallback that emits `Reset`); ConflictSync
digest-driven state sync (2025): designed for state-based CRDTs without a
journal, we have a journal; Plumtree/HyParView gossip broadcast (Leitão et
al., SRDS 2007): duplicate-copy suppression and membership sampling for a
large epidemic overlay, while ours is a fixed authenticated full mesh of at
most 5 peers (`PRODUCTION_MAX_PEERS`), fan-out is already exactly n−1 unicast
payload copies, graft is already covered by the bounded requested
`(origin, seq)` repair window plus RBSR, and membership is
orchestrator-owned and fail-closed (ADR 0041), so there is no lazy branch to
add, nothing to prune, and no overlay to sample.

Plumtree is revisited only if active Regions approach the transport mesh
limit (full mesh at `PRODUCTION_MAX_PEERS` = 5, far under `REGION_CEILING` =
20) or a push-broadcast fan-out requirement appears (e.g., D-57
whole-database client replicas), where eager payload on tree branches with
lazy message-IDs elsewhere would beat n−1 unicast copies per receiver.

## 2. Replication engine

### 2.1 Inside a Region: physical follower (unchanged, ADR 0041)

RF=2 by WAL-frame shipping; `RegionalDurable` = follower `fdatasync` ack;
promotion = open the file under a new replica epoch. No logical replay.

### 2.2 Across Regions: causal+ over a per-origin journal

Every Region is a writer for every database it hosts. A **journal tailer**
per (database, peer Region) ships committed rows in `seq` order, so delivery
from one origin is FIFO by construction. Cross-origin causality uses the row's
`context` (dotted causal context, ADR 0022): a receiver applies a row only
when every dot in its context is already applied locally (COPS dependency
check, cheap here because contexts summarize to a per-Region frontier vector
`{origin → seq}`). A frame with an unsatisfied dependency is dropped and
reported as deferred; it is not queued. The bounded RepairDriver then
reconciles the missing source-backed publication through anti-entropy, after
which the sender may replay the deferred frame, never a skip.

Apply rule per row (D-36):

1. **Clean apply** (the local version's dot is in the incoming context): write
   bytes, no parse.
2. **Concurrent** (neither context contains the other's dot): parse both
   bodies, merge per top-level field with remove-wins map semantics, HLC
   tiebreak within a field, write the merged canonical bytes with a merged
   context, record both inputs and the winning rule in history with a
   deterministic explanation.
3. **Merge-schema collections**: typed fields (PN-counter, add-wins /
   remove-wins set, MV register) merge by their algebra; `text` fields
   (post-launch) merge by Eg-walker event-graph replay stored per field.
4. **Dominated** (incoming dot already in local context): drop, idempotent.

Merge is a pure function of the set of observed rows (Melda's deterministic
reconstruction property), so any delivery order, duplication, or partial
replay converges; the simulation harness asserts this directly.

### 2.3 Durability classes

- `RegionalDurable`: ack after the local RF=2 follower fsync. The write is
  causally visible in its Region immediately and in others after tailing.
- `GlobalDurable`: ack only after every selected Region has durably accepted
  the row (its own follower fsync). Following UNISTORE, a `GlobalDurable` row
  is only issued when every dot in its context is itself *uniform* (known
  accepted in all selected Regions); otherwise the writer first waits for
  those dependencies to become uniform, then commits. With fewer than two
  reachable selected Regions it fails closed (ADR 0035).

### 2.4 Session guarantees (D-37)

The **session token** is the client's causal timestamp: the frontier vector
`{origin → seq}` of everything the client has observed, plus the HLC of its
last write. Default guarantee is **read-your-writes + monotonic reads**
(Occult's observable causal consistency): a member serving a read compares
its applied frontier with the token; if it is behind on any origin it waits
up to a bounded time for the tailer, then either serves or returns a typed
`StaleReplica` with the Region that can serve (the SDK follows it). Per
request, the SDK may lower to `eventual` (no check) or raise to
`writes-follow-reads`; there is no linearizable option (leaderless, D-37).

### 2.5 Anti-entropy: range-based set reconciliation

Per database, the reconciled set is `{(origin, seq) → row_hash}` where
`row_hash` covers the canonical bytes and causal metadata. Each member keeps a
**range-summarizable aggregate**: a fixed-fanout summary table over `seq`
buckets per origin (e.g. 4,096 rows per leaf bucket, 64-ary above) storing an
additive fingerprint (sum of 128-bit hashes mod 2^128, following Meyer's
analysis of additive fingerprints), maintained in the same transaction as the
row. Two members reconcile a database by exchanging fingerprints for ranges,
splitting mismatching ranges (Negentropy-style wire messages: `Fingerprint`,
`IdList`, `Skip`) until leaf ranges differ, then shipping missing rows through
the normal apply path. Properties we rely on: rounds are O(log n) in the
worst case, a round trip carries O(k log n) bytes for k differences, the
protocol needs no per-connection state and cannot be forced into full
transfer by pathological data. It runs after every tailer reconnect, after
region bootstrap, on the dependency-queue overflow above, and on a slow
periodic sweep; the former tree-based reconciler is deleted.

### 2.6 Region bootstrap and repair

A joining Region restores the newest native TerraBase backup snapshot
(ADR 0046) of each database into its own Object Storage (residency, D-52), opens it, tails all peers
from the snapshot's frontier vector, then runs one full reconciliation to
prove equality before placement marks it serving.

### 2.7 Transport

Pooled QUIC lanes per peer Region; one frame per committer wave per peer;
zstd with a dictionary trained on canonical JSON; credit-window pipelining;
same-document rows coalesced within a wave (last bytes + merged context).
Frames are independent of member internals so a future authorized
whole-database client replica (D-57) can consume them.

## 3. Sync engine

### 3.1 Model: views as Z-set streams

Following DBSP, a **shared view** is a query `Q` applied to the stream of
database snapshots; its output stream's differences are **Z-sets**: bags of
`(row, weight)` with weight `+1` (enter result) or `-1` (leave result). A
committed journal row is itself a Z-set delta on its collection: an insert is
`{(new,+1)}`, a delete `{(old,-1)}`, an update `{(old,-1),(new,+1)}`. The
engine computes `ΔV = Q^Δ(ΔD)` per cadence window (100 ms, D-14) and appends
it to the view's immutable log, encoded once.

### 3.2 The launch live subset, derived from DBSP

DBSP incrementalizes plans operator by operator; the cost of each operator's
incremental form decides what we ship live at launch (D-78):

| Operator | Incremental form | State | Launch |
| --- | --- | --- | --- |
| `WHERE` (filter, projection, JSON path extraction) | linear: `σ(ΔD)` | none | live |
| FTS5 `MATCH` | linear over the FTS index delta | none | live |
| `ORDER BY … LIMIT k` | top-k with boundary buffer k' = 2k (Yi et al.); refill from the index only when the buffer drains | O(k') | live |
| `JOIN` | bilinear: `ΔA ⋈ B + A ⋈ ΔB + ΔA ⋈ ΔB` | integrated inputs | post-launch |
| `GROUP BY` / aggregates | needs the integral of the group | per-group state | post-launch |
| subqueries, window functions | composition of the above | as above | post-launch |

A statement outside the subset returns `NotSubscribable` naming the operator;
`execute()` still runs it. Joins and aggregates arrive after launch on the
same Z-set contract with integrated state bounded per view (they are why D-64
caps distinct views).

### 3.3 Dependency matching

Views register their predicates in an interval tree keyed by
`(collection, index_id, range)` over extracted columns (D-31). Each committed
row is matched using extracted values already in hand from the write path,
producing the set of affected views without evaluating any SQL. For a
matched view, the row's Z-set delta passes through the view's compiled
filter/projection; for top-k views it is applied to the boundary buffer.
Cost per write is proportional to affected views, never to attached readers
(D-22).

### 3.4 Consistency of a revision

Each view revision is computed from one journal cut `(applied frontier
vector, seq_hi)` and carries it in its checkpoint. Within a view, a client
never observes half a committed row. **Across views we promise nothing**:
two subscriptions may be at different cuts (IntersectionCC: guarantees hold
on the intersection of interest sets; separate views have separate interest
sets). Applications that need one consistent cut over several collections
use one view with collection sync (D-38). Documented in the SDK.

### 3.5 Authorization

The access rule compiles into the view plan (D-39/D-78); the view identity
binds the rule revision and the entitlement epoch (D-46). A rule change
bumps the generation: the old log stays readable through retention and is
then purged by cache tag (D-55). Permission loss for a principal removes
protected rows before the new generation advances. The capability header
(D-14) is the only thing that gates cached bytes; identical authorization
scope ⇒ identical capability ⇒ shared cache key.

### 3.6 Checkpoints, resume, reset

A checkpoint binds view identity and generation, database/branch
incarnation, schema version, rule revision, entitlement epoch, Region and
replica epoch, the journal cut, and expiry. Resume with retained log
revisions sends them; resume below the retained floor, or with any bound
value changed, returns a typed reset reason and a fresh authorized snapshot.
No gap is ever filled by guessing. Because the cut is a frontier vector, a
client that pinned to Region A and fails over to Region B resumes exactly at
the same causal position.

### 3.7 Delivery

Encode-once immutable log responses with deterministic chunk boundaries and
byte-identical payloads per `(view, generation, offset)`; origin
`singleflight` per key; `Cache-Control: private, max-age=0` with
`Cloudflare-CDN-Cache-Control: public, max-age=<retention>, immutable`;
`no-store` on empty long-poll timeouts (D-14/D-55). Optional WebSocket and
webhooks consume the same revisions.

## 4. Invariants (simulation harness, E-12)

1. No acknowledged write is lost under member loss, Region loss, WAN
   partition, clock skew ≤ 500 ms, duplicate or reordered delivery.
2. All Regions hosting a database converge to byte-identical canonical bodies
   and equal reconciliation fingerprints at quiescence.
3. Merge is order-independent: any permutation of the same journal rows
   yields the same state and the same history explanations.
4. A `GlobalDurable` ack implies the row and all its causal dependencies are
   present in every selected Region.
5. Read-your-writes holds for any client carrying its session token, across
   Region failover.
6. `view revision = Q(database at its cut)` for every revision of every view.
7. Cached log bytes equal origin bytes for the same key; no bytes ever cross a
   capability boundary.
8. Reconciliation after any fault completes in O(log n) rounds and ships
   only the differing rows.

## 5. Consequences

- `terrabase-replication-core` keeps the range-summary aggregate and the
  reconciliation kernel only; the progress kernels, the former tree
  comparison, and the repair planner are deleted.
- The SQL planner tags each plan `live` or `execute-only` from the operator
  table in 3.2; that tag is the whole `NotSubscribable` decision.
- The `session token` format is public SDK contract (opaque bytes, but
  versioned) and appears in the conformance suite (D-13).
- History entries for merges record the rule applied (`remove-wins`,
  `hlc-tiebreak`, typed algebra) so the conflict viewer (D-48) can explain
  every outcome without recomputation.

## References

- Lloyd, Freedman, Kaminsky, Andersen. *Don't Settle for Eventual: Scalable Causal Consistency for Wide-Area Storage with COPS.* SOSP 2011.
- Mehdi, Littley, Crooks, Alvisi, Bronson, Lloyd. *I Can't Believe It's Not Causal! Scalable Causal Consistency with No Slowdown Cascades* (Occult). NSDI 2017.
- Ma, Wei, et al. *Tunable Causal Consistency: Specification and Implementation.* arXiv 2211.03501, 2022.
- Bravo, Gotsman, de Régil, Wei. *UNISTORE: A fault-tolerant marriage of causal and strong consistency.* USENIX ATC 2021.
- Almeida, Shoker, Baquero. *Delta State Replicated Data Types.* JPDC 2018.
- Brocco. *Introducing Support for Move Operations in Melda CRDT.* arXiv 2503.04811, 2025; *A Composable CRDT Layer for Byzantine-Resilient Deterministic Reconstruction.* arXiv 2606.18966, 2026.
- Gentle, Kleppmann. *Collaborative Text Editing with Eg-walker: Better, Faster, Smaller.* EuroSys 2025.
- Meyer. *Range-Based Set Reconciliation.* arXiv 2212.13567, 2023; *Range-Based Set Reconciliation via Range-Summarizable Order-Statistics Stores.* arXiv 2603.19820, 2026; Negentropy protocol v1 (logperiodic.com/rbsr.html).
- Budiu, Chajed, McSherry, Ryzhyk, Tannen. *DBSP: Automatic Incremental View Maintenance for Rich Query Languages.* VLDB 2023; extended in The VLDB Journal 34:39, 2025.
- Yi, Yu, Yang, Xia, Chen. *Efficient Maintenance of Materialized Top-k Views.* ICDE 2003.
- *A framework to reason about consistency and atomicity guarantees in a sparsely-connected, partially-replicated peer-to-peer system* (IntersectionAtomicity / IntersectionCC). arXiv 2603.03899, 2026.
- Kulkarni, Demirbas, Madappa, Avva, Leone. *Logical Physical Clocks and Consistent Snapshots in Globally Distributed Databases* (HLC). 2014.
