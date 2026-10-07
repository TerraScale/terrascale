# Anna core: coordination-free storage and replication (TS-634)

- Status: design, proposed 2026-10-07. Nothing below is implemented unless the [slice plan](#16-slice-plan) marks
  the slice as merged. Statements about the future system are targets, not guarantees; each guarantee is claimed only
  once its slice ships with integration evidence.
- Ticket: TS-634 (Ku). User decisions of 2026-10-06 (scope, engine, replication, consistency, transactions, tiers,
  policy, cutover) and 2026-10-07 (replicate inside one region now, across regions later).
- Related: TS-602 (greenfield wipe, the cutover point), TS-606 (deployed benchmark, the qualification gate),
  TS-633 (continuous backup of the SQLite WALs), TS-565 (node pools in ts-orchestrator), TS-562 (NATS fabric between
  orchestrators), TS-581 (usage metering), TS-574/TS-575 (cross-member versions and write-wait).

Sources (read in full for this design):

1. **[Anna]** C. Wu, J. M. Faleiro, Y. Lin, J. M. Hellerstein, *Anna: A KVS for Any Scale*, ICDE 2018.
   <https://dsf.berkeley.edu/jmh/papers/anna_ieee18.pdf>
2. **[Anna-T]** C. Wu, V. Sreekanti, J. M. Hellerstein, *Autoscaling Tiered Cloud Storage in Anna*, PVLDB 12(6), 2019.
   <https://www.vldb.org/pvldb/vol12/p624-wu.pdf>
3. **[δ-CRDT]** P. S. Almeida, A. Shoker, C. Baquero, *Delta State Replicated Data Types*, arXiv:1603.01529.
   <https://arxiv.org/abs/1603.01529>
4. **[AFT]** V. Sreekanti et al., *A Fault-Tolerance Shim for Serverless Computing*, EuroSys 2020 (read atomic
   isolation; cited for the transaction design, named in the ticket).

## 1. Summary

TerraBase replaces its storage and replication core with an Anna-style core:

- **One actor per CPU core.** Each actor owns a private hash map of document states. Keys are hash-partitioned
  across actors; actors share no key state, take no locks on it and never wait for each other. They exchange
  merged deltas through queues ([Anna] §IV-V).
- **Lattice values.** Every stored value is a join-semilattice, so replicas converge whatever the delivery order,
  duplication or delay ([Anna] §III, [δ-CRDT] §2).
- **Delta-state replication.** Writes produce small deltas. Inside a region, nodes exchange delta-intervals over the
  existing mutual-TLS gRPC peer links with the causal anti-entropy algorithm of [δ-CRDT] (Algorithm 2). Membership
  and the ring come from ts-orchestrator's NATS.
- **Consistency per request.** Last-writer-wins on hybrid logical clocks (the default, matching today's merge) or
  causal, both served from one stored lattice.
- **Transactions.** Read-atomic multi-document transactions in the [AFT] style, plus keyed compare-and-set
  (`If-Match`) through one owner per key range.
- **Tiers.** A memory tier (the actors' maps) over a disk tier: SQLite, one file per database per node, which is
  also the query and index engine. TS-633 keeps shipping those files' WALs.
- **Policy engine.** Monitoring, hot-key selective replication, promotion and demotion between tiers, and node
  autoscaling through ts-orchestrator within a budget cap ([Anna-T] §3-5).
- **Regions.** The ring, membership and replication policy carry region and zone from day one. Only the
  intra-region path is built now; the cross-region (WAN) path is specified and stubbed ([§11](#11-multi-region-later)).

The public HTTP API, collections, SDK calls and the dashboard keep working. Where a guarantee changes, the
[compatibility matrix](#13-api-compatibility-matrix) says so. The new core runs behind the `core` switch
(`sqlite`, the default, or `anna`), is qualified with the TS-606 benchmark and becomes the only core at the TS-602
greenfield wipe. There is no data migration code.

### Non-goals

- A TerraBase-owned B-tree or LSM engine. The memory tier is a hash map of lattice states; durable storage stays
  SQLite (decision [0002](../decisions/0002-sqlite-storage-engine.md), `AGENTS.md`).
- Linearizable reads or serializable multi-document transactions. Both need coordination on every request, which
  this design removes on purpose ([Anna] §II-B). Keyed compare-and-set is the one coordinated operation.
- Anna consistency levels beyond LWW, causal and read-atomic (item-cut, PRAM and others) in the first slices. The
  lattice composition admits them later ([Anna] §VI-D).
- The cross-region path, in the first slices.

## 2. Architecture

```text
             client (HTTPS, ts-auth JWT)
                       |
   +-------------------v--------------------------------------------- node (region gru, zone ad-1) --+
   |  Kestrel + TerraScale.Endpoints: authenticate, validate JSON, compute key hash                   |
   |        |                                         |                                               |
   |        | not a replica of this database          | replica                                       |
   |        v                                         v                                               |
   |  forward over peer gRPC              ActorRouter: key hash -> actor (local ring)                  |
   |                                        |            |            |                               |
   |                                  +-----v---+  +-----v---+  +-----v---+   one actor per core,      |
   |                                  | actor 0 |  | actor 1 |  | actor N |   private map of states,   |
   |                                  +---+-----+  +---+-----+  +---+-----+   inbox = bounded queue    |
   |                                      |  deltas (+ completion)  |                                   |
   |                                  +---v------------------------v---+                               |
   |                                  | flusher per database: group    |  disk tier (SQLite file per   |
   |                                  | commit -> SQLite, assigns the  |  database): lattice states,   |
   |                                  | delta-interval sequence c_i    |  materialized JSON, indexes   |
   |                                  +---+----------------------------+                               |
   |                                      | committed delta-intervals                                  |
   |                     +----------------v-----------------+      +----------------------------+      |
   |                     | anti-entropy (local path, §7)    |<---->| peers in the region (mTLS) |      |
   |                     | WAN shipper (later, §11, stub)   |      +----------------------------+      |
   |                     +----------------------------------+                                          |
   |  membership + ring (NATS KV, §6)   policy agent (§10)   SQL / queries / shapes on SQLite (§12)   |
   +---------------------------------------------------------------------------------------------------+
```

Every node runs the same binary and plays every role: request proxy ([Anna-T] routing tier), storage kernel,
anti-entropy endpoint and policy agent. [Anna-T] splits routing, monitoring and storage into separate stateless
node kinds; TerraBase keeps one kind of member because a cell has three to a few dozen members and ts-orchestrator
places one workload profile. One member at a time is elected policy leader through a NATS KV lease ([§10](#10-tiering-and-policy-engine)).

### Code layout

Inside the server project (`src/TerraBase`), following `AGENTS.md`:

| Folder | Holds |
| --- | --- |
| `Engine/Lattices/` | The lattice library: pure values and joins, no I/O (slice 1) |
| `Engine/Anna/` | Actors, router, local ring, membership client, anti-entropy, WAN interfaces, policy agent. No SQLite types |
| `Engine/Sqlite/` | The disk tier: an implementation of the new `IDiskTier` interface, alongside today's engine |
| `Features/*` | Unchanged endpoints. They consume `IDocumentService`, `IShapeService`, `ISqlExecutor` and friends; the `core` switch picks the implementation in `Program.cs` |

`core=anna` swaps service implementations in the composition root. No endpoint, request or response type changes
for the switch itself; the additive headers and response members in [§8](#8-consistency-per-request) come with
their slices.

## 3. Actors in .NET

### Threads and pinning

- `actors` (setting, default `Environment.ProcessorCount`, which already honors container CPU limits) dedicated
  threads, created with `new Thread(..., maxStackSize)` and `IsBackground = true`, never thread-pool threads. Each
  runs one actor for the process lifetime.
- **Pinning** is optional (`actor-pinning`, default off until the TS-606 runs show migration cost). On Linux the
  actor thread calls `sched_setaffinity(0, …)` on itself through `LibraryImport("libc")`; the mask respects the
  cgroup cpuset. .NET has no managed per-thread affinity on Linux (`ProcessThread.ProcessorAffinity` is
  not supported on Linux). Pinning needs `AllowUnsafeBlocks` for the generated marshalling; ask a human before enabling it.
- Kestrel, JSON parsing, the SQLite storage threads and gRPC run on the thread pool and the existing
  `storage-threads`. Actors park when idle, so they do not starve those threads; the TS-606 runs tune `actors`.

### Inbox and event loop

- Each actor has one inbox: a `Channel<ActorMessage>` with `SingleReader = true`, bounded by count **and** bytes
  (`AGENTS.md`): the sender reserves the message's bytes against the actor's inbox budget and `IMemoryBudget`
  before `TryWrite`, the actor releases them after handling. A full inbox answers `503 capacity` with
  `Retry-After`, like today's `storage-queue`.
- The channel's internal lock guards only the queue, never key state. If profiling shows contention on it, replace
  it with a lock-free MPSC ring; nothing else changes.
- The loop, without `async` inside the actor and without blocking on tasks:

  ```text
  loop:
    drain up to B messages from the inbox (TryRead): handle each against the private map
    if the gossip epoch elapsed: send merged deltas of hot keys to sibling actors (§7.1)
    if the inbox is empty: park on a wait handle with timeout = time to the next epoch
  ```

- Requests carry a `TaskCompletionSource` created with `RunContinuationsAsynchronously`. The actor never awaits:
  work that needs I/O (a cold-key load, a flush) is handed to the disk tier with a callback that posts a message
  back to the actor. Lattice joins make the order of that message and concurrent deltas irrelevant.

### Memory

- Per-key state: the document's lattice ([§4](#4-lattice-library)) with its JSON bodies as immutable UTF-8
  `byte[]`, plus bookkeeping (`pending` deltas, last access, access count). The key is `(database id, collection,
  id)` interned per actor; the hash is computed once at the edge.
- Every inserted or grown entry reserves its bytes from `IMemoryBudget` before it is materialized and releases
  them on eviction, error or cancellation. The memory-tier share replaces today's document cache share
  ([memory](../memory.md)); today's `DocumentCache` is not used under `core=anna`.
- Eviction is CLOCK over clean keys (no pending deltas). The [policy engine](#10-tiering-and-policy-engine) picks
  promotion and demotion; CLOCK is the fallback when the share is full.
- Hot paths parse JSON once at the edge (`RequestJson`), keep bodies as bytes and use pooled buffers for wire
  frames. GC settings stay at the server defaults until a measurement asks for more.

## 4. Lattice library

`Engine/Lattices/` (slice 1) is a small set of immutable value types with a join. A lattice `L` has a bottom `⊥`
and a join `⊔` that is associative, commutative and idempotent (ACI), with `x ⊔ ⊥ = x`; `x ⊑ y` iff `x ⊔ y = y`
([Anna] §III). Composite lattices are ACI when their parts are, so the laws are checked per building block
([Anna] §VI-A).

```csharp
public interface ILattice<TSelf> where TSelf : ILattice<TSelf>
{
    static abstract TSelf Bottom { get; }
    TSelf Join(TSelf other);   // least upper bound; never mutates this or other
}
```

| Type | State | Join | Used for |
| --- | --- | --- | --- |
| `MaxLattice<T>` | a totally ordered value | `max` | version counters, sequence numbers, ack positions |
| `Hlc` + `LwwRegister<T>` | `(Hlc, ReplicaId, T)` | greater `(Hlc, ReplicaId)` wins ([δ-CRDT] Fig. 4, lexicographic pair) | ring and policy metadata ([Anna-T] §4.3.2) |
| `SetLattice<T>` | grow-only set | union ([δ-CRDT] Fig. 5) | committed transaction ids, member numbers seen |
| `MapLattice<TKey, TValue>` | key → lattice | union of keys, join of values ([Anna] §VI-B) | an actor's state, a delta-group, a delta-interval |
| `Dot`, `CausalContext` | `(ReplicaId, n)`; version vector plus a cloud of extra dots | union, then compaction of contiguous cloud dots into the vector ([δ-CRDT] §7.2) | causal metadata |
| `MvRegister<T>` | `Causal<DotFun<T>>`: dot → value, with a causal context | [δ-CRDT] Fig. 12 (`DotFun`) | **document state** |
| `AwSet<T>` | `Causal<DotMap<T, DotSet>>` | [δ-CRDT] Fig. 12 (`DotMap`) | add-wins sets where a remove must not lose a concurrent add |
| `GCounter` | replica → count | pointwise max ([δ-CRDT] Fig. 2) | usage metering counters (TS-581), monitoring |

Every mutator is a **delta-mutator** `mδ(X)`: it returns the delta, and the new state is `X ⊔ mδ(X)` ([δ-CRDT]
Def. 1). The library exposes both, so callers ship deltas and never whole states except for catch-up.

### Clocks and identities

- `ReplicaId` = `(region number, member number, actor index)` packed in 64 bits (16 + 40 + 8). Member numbers
  come from the member's first NATS KV revision ([§6](#6-membership-and-gossip)); region numbers from the cell
  registry. Including the region makes dots globally unique when regions start exchanging deltas ([§11](#11-multi-region-later)).
- `Hlc` = 48-bit Unix milliseconds and a 16-bit logical counter in one `ulong`, updated on every local event and on
  every received delta; a remote stamp more than `max-clock-skew` (default 1 s) ahead of the local clock is
  refused with a metric instead of dragging the clock forward.

### Documents

A document is one `MvRegister<DocumentVersion>` keyed by `(database, collection, id)`:

```text
DocumentVersion = (Hlc stamp, TxId? tx, Body)        Body = UTF-8 JSON object bytes | Tombstone
```

- **Write** (put, patch result, create): `dot = (self, ctx.Max(self) + 1)`;
  delta = `({dot → v}, observed ∪ {dot})` ([δ-CRDT] Fig. 14). `observed` is what the write replaces: the actor's
  whole context for a LWW write, the client's context for a causal write ([§8](#8-consistency-per-request)).
- **Delete** writes a version whose body is `Tombstone`. A pure `clear` (empty store) would let an older concurrent
  write survive a LWW delete, so deletes keep a tombstone until it is stable ([§7.4](#74-garbage-collection)).
- **Values with the same dot are the same value** (dots are unique), so `DotFun` needs no value join; the library
  asserts equality in debug builds and keeps the left value.
- Per-key contexts stay compact: a writer always includes its full context of the key, so per-replica dots of a key
  are contiguous and the context compresses to a version vector; the cloud only holds dots from a causal write
  with a partial context until anti-entropy fills the gap.

Why the whole document is one register: PUT replaces whole documents, today's cross-member merge is already
whole-document LWW ([replication](../replication.md), merge policy v1), queries and indexes need one materialized
JSON per document, and a register keeps the version token cheap. A JSON CRDT (`ORMap<path, MvRegister>` with
counters for `inc`) is possible with the same library, opt-in per collection, later; it is not in the slice plan.

Collections and index definitions stay documents of the reserved `_collections` and `_indexes` collections, as
today, so they replicate like documents. The database registry and backup policies are documents of the `_system`
database ([§9.3](#93-catalog)). Usage counters are `GCounter`s keyed by `(database, hour)`.

### Encoding

Lattices persist in SQLite and travel on the wire, so the encoding is versioned and independent of .NET layout and
endianness (`AGENTS.md`): a format byte, little-endian fixed-width integers, LEB128 lengths, dots sorted by
`(replica, n)`, so equal states encode to equal bytes. The version token and ETag hash these bytes
([§8.3](#83-version-tokens)). The encoding ships with the disk tier (slice 3), not slice 1.

## 5. Ring, placement and replication factor

Two rings, as in [Anna-T] §4.3.1, with one deliberate change: the global ring places **databases**, not keys.

- **Global ring (per region).** Consistent hashing with virtual nodes ([Anna-T] §4.2): each active member owns
  `64 × weight` tokens `XxHash3(member name ‖ i)`. A database's **disk replicas** are the first `rf` distinct
  members clockwise from `XxHash3(database id)`, choosing members in distinct zones first. TerraBase places whole
  databases because every query, index and shape runs on one node's SQLite ([§12](#12-queries-indexes-shapes-and-backups));
  per-key placement would turn every query into a scatter-gather. A database too large or too hot for `rf` nodes is
  split into hash-range shards `(database, range)` with the same machinery and scatter-gather queries; that is a
  later slice, not the first cut.
- **Local ring (per node).** Keys go to actor `XxHash3(key) mod actors`. A key replicated over `t` actors lives on
  actors `(h + i) mod actors`, `i < t`. The actor count is fixed per process; the memory tier is rebuilt from the
  disk tier after a restart, so a different count after a restart costs nothing.
- **Primary.** The first disk replica is the database's primary; the key's owner actor is its `h mod actors` actor
  on the primary. Owners serve compare-and-set ([§9.2](#92-keyed-compare-and-set)).
- **Replication policy** per database, stored in its registry document:

  ```json
  {"home_region": "gru", "rf": 3, "remote": []}
  ```

  `rf` disk replicas inside the home region now; `remote: [{"region": "sfo", "rf": 2}]` later ([§11](#11-multi-region-later)).
  Default `rf = min(3, active members)`, so a 3-node cell keeps every database on every node, as today. A plan
  (ts-dashboard) may raise it later.
- **Replication vector** per hot key, after [Anna-T] §4.3.1: `(memory replicas across members, actors per member)`,
  set by the policy engine, stored as `LwwRegister`s in `_system` and replicated to every member. Cold keys use the
  default `(0 extra members, 1 actor)`.
- **Ring epoch** = the highest NATS KV revision of the membership bucket a member has applied. Members compare
  epochs in every peer frame; a request that reaches a member that is no longer a replica is forwarded to a current
  replica ([Anna] §VII-C, redirects).

## 6. Membership and gossip

Membership and the ring come from ts-orchestrator's NATS (JetStream KV), the store `AGENTS.md` (umbrella) names for
TerraBase's external persistence. TerraBase stores no member data elsewhere.

- **Bucket** `terrabase_members_<cell>`, one key per member:

  ```json
  {"name":"gru-1-db-1","incarnation":"…","peer":"10.251.0.21:9443","region":"gru","zone":"ad-1",
   "actors":4,"weight":1,"tier":"disk","state":"active","started":"…"}
  ```

- **Heartbeat.** Each member rewrites its key every `member-heartbeat` (2 s); the bucket's `max_age` (10 s) expires
  silent members. Members watch the bucket and rebuild the ring on every change. Settings: `nats-url`,
  `nats-credentials-file` (NATS user credentials, never logged), `cell`.
- **Member number.** The KV revision of a member's first `create` of its key, persisted in its data directory; a
  member whose data directory is lost re-registers and gets a new number, so dots are never reused.
- **Join** ([Anna] §VII-C, [Anna-T] §4.3.3): register as `joining`; every member recomputes the ring, and current
  replicas of the databases now placed on the joiner ship their full state through anti-entropy ([§7](#7-delta-anti-entropy));
  once it has every database it switches to `active` and starts serving.
- **Leave**: `leaving`; the ring drops the member as a replica, the others take over its databases, it keeps
  answering anti-entropy until every database it held has its replicas acked, then deletes its key and exits.
- **Failure**: an expired key removes the member from the ring. Its databases get a new replica (full state from
  the survivors); ts-orchestrator replaces the node. A member partitioned from NATS keeps its last ring.
- **Leases.** A member's key doubles as its lease. A member serves owner operations (CAS) only while it renewed its
  key less than `lease-ms` (8 s) ago by its own monotonic clock; a new primary serves them only once the old
  member's key expired. This relies on bounded clock drift, stated in [failure modes](#14-failure-modes).
- **Gossip** of data never uses NATS: deltas go over the peer gRPC links ([§7](#7-delta-anti-entropy)). NATS
  carries membership, leases and policy statistics only.

For tests and single-node runs, the existing static `peers` setting is a second `IMembership` implementation: a
fixed member list, no leases, so no CAS failover.

## 7. Delta anti-entropy

### 7.1 Inside a node

Actors of the same key (hot keys replicated over `t` actors) exchange merged deltas each epoch (`gossip-epoch-ms`,
default 10 ms): [δ-CRDT] Algorithm 1 in direct mode with merge-at-sender ([Anna] §V-A): many writes to a hot key in
one epoch travel as one joined delta. Only committed deltas are gossiped ([§9.1](#91-write-path-and-durability)).

### 7.2 Local path: between members of a region

The replica of [δ-CRDT] Algorithm 2 is **one database on one member**. Its flusher ([§9.1](#91-write-path-and-durability))
already serializes every committed delta of the database, so it numbers them:

- **Durable state** `X_i` = the database's SQLite file; **durable counter** `c_i` = the last delta-interval
  sequence, written in the same SQLite transaction as the deltas it numbers (Algorithm 2 requires `c_i` durable).
- **Deltas** `D_i` = the `intervals` table: `seq → delta-group` (a `MapLattice<DocKey, MvRegister>` with its
  transaction ids). Algorithm 2 keeps `D_i` volatile; TerraBase keeps it on disk because shapes read it as their
  change log ([§12](#12-queries-indexes-shapes-and-backups)) and catch-up after a restart then needs no full state.
- **Acks** `A_i` = per neighbor, the highest sequence it acknowledged. Volatile, as in the paper.
- **Send**: after every group commit (low latency, not periodic) and on a 1 s timer, for each other disk replica
  `j` of the database: if `A_i(j) < c_i`, send `(delta, D_i[A_i(j)..c_i), c_i)`; if `D_i` no longer holds
  `A_i(j)`, send the full state `X_i` as a snapshot stream.
- **Receive** `(delta, d, n)` from `j`: if `d ⋢ X_i` (some dot of `d`'s contexts is not covered by `X_i`), join it,
  append it to `D_i` as a new interval (transitive mode: Algorithm 2 line 12, required for the causal
  delta-merging condition when writes happen on several members), commit, then reply `(ack, n)`. The ack goes out
  only after the receiver's commit with `synchronous = FULL`, so it is today's `DurableAck`
  ([0010](../decisions/0010-write-wait-and-synced-durable-ack.md)) and write-wait keeps its meaning.
- **Ack** `(ack, n)`: `A_i(j) = max(A_i(j), n)`; duplicates and reordering are harmless.

Algorithm 2 joins `Δ_j^{a,b}` at `i` only after `i` acked `a`, so `X_i ⊒ X_j^a`: the causal delta-merging
condition ([δ-CRDT] Def. 6, Prop. 3). Every member's database state is then a state of the equivalent state-based
CRDT, which gives causal consistency for the whole database replica, across its keys.

Wire format: the existing versioned application envelope with LZ4 ([0007](../decisions/0007-lz4-application-envelope-replication.md),
[0001](../decisions/0001-grpc-lz4-transport-compression.md)) over the existing peer listener and `peer-*` TLS
settings. A new message family in `replication.proto` (`terrabase.v1`, additive fields and messages) carries
intervals, acks and snapshots; the old change-group messages go with the old core at cutover.

Bounds: every send buffer and in-flight window is capped by intervals and bytes (`AGENTS.md`); a neighbor that falls
beyond the retained window gets the full state instead of an unbounded backlog.

### 7.3 Memory replicas

Members that hold a hot key only in memory (no SQLite file of its database) receive its committed deltas with
Algorithm 1 from the key's primary. They serve LWW reads of that key. They are outside Algorithm 2, so a causal read
there checks coverage first ([§8.2](#82-causal)).

### 7.4 Garbage collection

- An interval leaves `D_i` once every neighbor acked it and the shape retention window passed.
- A tombstone version is dropped, with its key, once the database's every disk replica acked an interval at or past
  it ([Anna] §VII: free a key once every replica's clock passed the delete). After that, no delayed delta can
  resurrect it: Algorithm 2 delivers nothing older than an acked position.
- Retired replica ids stay in version vectors; trimming them needs every replica to agree they are retired and is
  left until measurement shows the vectors grow.

## 8. Consistency per request

The request header `X-TerraBase-Consistency: lww | causal` picks the level per request (default `lww`); a
transaction applies it to all its operations. Both levels read and write the same `MvRegister`, so mixing them on
one key is defined.

### 8.1 LWW (default)

- **Read**: the version with the greatest `(Hlc, ReplicaId)`; a tombstone winner reads as `404`. Same answer on
  every replica holding the same state.
- **Write**: replaces every version the serving actor knows (`observed` = its whole context). Two concurrent writes
  on two replicas both survive in the register until a later write; reads already pick the later stamp, as a
  classic LWW register would.
- Matches today's cross-member semantics (merge policy v1: higher version wins). The stamp is now an HLC rather than
  a per-database commit offset.

### 8.2 Causal

- **Read**: every live version. One live version is `"state":"value"`, as today; several are
  `"state":"conflict"` with a new `siblings` array (`[{"version","document"}]`), and the top-level `document` holds
  the LWW winner so clients that ignore `siblings` keep working. A deleted sibling appears as `{"version",
  "deleted":true}`.
- **Write**: replaces exactly the versions the client saw: `observed` = the context in its version token (`If-Match`
  or the body's `version`). Without one, the write is concurrent with every stored version and adds a sibling,
  as in Dynamo and Riak.
- **Reads after**: `X-TerraBase-After: <version token>` makes a read wait until the serving replica's state covers
  the token's context (at most `write-wait-ms`), else forward to the primary. That gives read-your-writes and
  monotonic reads across members for clients that pass their last token; without it, causal reads are causal for
  the serving replica's state ([§7.2](#72-local-path-between-members-of-a-region)).
- **Scope**: causal per region now. [§11](#11-multi-region-later) says how contexts cross regions later.

### 8.3 Version tokens

- `version` and `ETag` become `tbv2-<base64url(context)>.<hash>`: the encoded causal context of the read state and
  the first 16 bytes of SHA-256 over the encoded register. Equal states give equal tokens on every replica, so
  TS-574's property (a version read on one member satisfies `If-Match` on another) holds.
- The token is opaque as today; its format changes once at the TS-602 wipe. Clients re-read before conditional
  writes, as with every earlier token change ([0004](../decisions/0004-document-api-v1.md)).
- A context too large for a header (more than 1 KiB encoded) keeps the hash and drops the context; a causal write
  with such a token answers `409 conflict` and asks the client to re-read with the body form. Rare: a context grows
  only with concurrent writers of one key.

## 9. Writes, transactions and compare-and-set

### 9.1 Write path and durability

1. The edge authenticates, validates the JSON (`RequestJson`, the same admission contract as today), hashes the key
   and finds the database's replicas. A non-replica member forwards the request over peer gRPC.
2. The actor loads a cold key from the disk tier first (one SQLite point read on a storage thread; a missing row is
   `⊥`), then applies the delta-mutator. The delta goes into the key's `pending` list, not its committed state.
3. The actor hands the delta and the completion to the database's flusher. Flushers **group-commit**: one SQLite
   transaction per `flush-ms` (2 ms), 512 deltas or 4 MiB, whichever comes first. The transaction joins each delta
   into the stored state, rewrites the materialized row, appends the delta-interval and advances `c_i`
   (`synchronous = NORMAL`, as today, [0003](../decisions/0003-sqlite-synchronous-normal.md)).
4. On commit the flusher posts `Committed(batch)` to the actors; each joins its deltas into committed state and
   drops them from `pending`. On failure it posts `Failed(batch)`; the actors drop the deltas and the writes answer
   `503`. A failed write never takes effect later.
5. The response goes out after the local commit (`"durability":"local_durable"`, as today), then after write-wait:
   every active disk replica's ack of an interval at or past the write's, bounded by `write-wait-ms`, unchanged
   ([0010](../decisions/0010-write-wait-and-synced-durable-ack.md)).

Reads see committed state only, so no one reads a write that was not acknowledged. Owner-routed operations
evaluate against committed plus pending state, so two compare-and-sets in the same flush window see each other.

The single SQLite writer per file stays: one flusher per database. Group commit amortizes it across every actor's
writes (today: one SQLite transaction per client write). Merge-at-sender collapses many writes to one hot key into
one row write per flush.

### 9.2 Keyed compare-and-set

`If-Match`, `If-None-Match: *`, `PATCH` (read-modify-write, including `inc`) and generated-id creates go to the key's
**owner**: the owner actor on the database's primary ([§5](#5-ring-placement-and-replication-factor)). It evaluates the
condition against committed plus pending state and writes; it is the only place that serializes operations of a key.

- **Guarantee**: conditional writes and patches of one key are linearizable with each other while the primary holds
  its lease ([§6](#6-membership-and-gossip)). This is stronger than today, where each member checks `If-Match` in
  its own SQLite transaction and two members can both accept.
- **Not covered**: an unconditional `PUT` or `DELETE` that another replica accepts concurrently. Under LWW the
  greater stamp wins; under causal it becomes a sibling. Documented in the matrix.
- **Failover**: the new primary waits for the old member's lease to expire, then pulls every neighbor's intervals up
  to their current `c_j` before it serves owner operations, so it has seen every acknowledged write.

### 9.3 Catalog

The `_system` database (database registry, backup policies, saved queries, usage counters, replication vectors)
uses `rf = all members` and the same lattices. Uniqueness (a database name per project) is an owner-routed
`If-None-Match: *`. Today's catalog primary and followers ([0009](../decisions/0009-catalog-replication.md)) are
not needed under `core=anna`: every member reads its local replica, and the owner serializes conflicting creates.

### 9.4 Read-atomic transactions

`POST D/transactions` keeps its request: up to 1024 operations, at most one per document. Isolation becomes **read
atomic** ([AFT]): no reader, inside one request, sees some of a transaction's writes and not others.

1. **TxId** = `(Hlc, ReplicaId)` of the coordinator, the disk replica that serves the request (others forward).
2. **Conditions** (`if_match`, `if_none_match`, `assert`) go to each key's owner as `Prepare(tx, key, condition,
   deadline)`. The owner checks the condition, then holds a **reservation** on the key until `Commit(tx)`,
   `Abort(tx)` or the deadline (`transaction-reservation-ms`, 2 s): other owner operations on the key wait behind it
   (bounded queue) or fail `409` after the deadline. Unconditional transactions skip this step.
3. **`assert_count`** runs on the coordinator's SQLite snapshot. It is not reserved: a concurrent insert can change
   the count before commit (phantoms). Documented as weaker.
4. **Commit**: if every prepare passed and the deadline has not passed, the coordinator writes the transaction's
   whole delta-group to its flusher as **one** interval in **one** SQLite transaction, tags every version with the
   TxId, then sends `Commit(tx)` to the owners. Otherwise it sends `Abort(tx)` and nothing is written.
5. **Visibility**: every member joins an interval in one SQLite transaction before any actor exposes its keys, so
   queries, lists and `assert_count` never see half a transaction. Point reads inside one request (asserts) apply
   the [AFT] rule: once a read returns a version of transaction `T`, later reads of `T`'s other keys return `T`'s
   version or newer, taken from the member's in-flight transaction table if the key's actor has not joined `T` yet.
6. **Crash windows**: a coordinator that dies before its commit leaves reservations that expire; nothing was
   written. A coordinator never commits after the deadline (it checks its monotonic clock before the flush), so an
   expired reservation cannot be followed by a late commit, under the same bounded-drift assumption as leases.

What transactions lose against today: serializable isolation on one member. They keep atomicity, all-or-nothing
conditions on the keys they name, and no fractured reads. Today, two transactions on two different members already
merge last-writer-wins, so cross-member serializability was never provided.

## 10. Tiering and policy engine

### 10.1 Tiers

| Tier | Where | Holds | Serves |
| --- | --- | --- | --- |
| Memory | actor maps on disk replicas, and memory replicas on other members | hot keys' lattice states | point reads and writes |
| Disk | SQLite file per database on each of its `rf` disk replicas | every key, its lattice state and materialized JSON, the intervals, indexes | cold point reads, queries, SQL, shapes, backups |
| Archive | object storage through TS-633 (WAL segments and snapshots) | backups, not online data | restores |

Unlike [Anna-T], whose disk tier is a set of separate nodes holding `k + 1` replicas while memory nodes cache hot
keys, every TerraBase disk replica also runs a memory tier. Fault tolerance is `rf` disk replicas (`k = rf - 1`);
memory replicas are extra and never count toward it.

### 10.2 Monitoring

- Each actor counts accesses per key in a Space-Saving top-k summary (bounded memory) and totals per epoch; each
  member publishes per-database and per-actor totals, its top keys, CPU, memory-tier occupancy, disk usage and
  request latency every 10 s on the NATS subject `terrabase.<cell>.stats.<member>` (core NATS, not persisted).
- Prometheus metrics on the `TerraScale.TerraBase` meter with bounded labels (actor index, tier, outcome; never
  database or key names): inbox depth, flush batch size and latency, pending bytes, interval lag per neighbor,
  hot keys tracked, promotions, demotions, replications, ring epoch, lease state.

### 10.3 Policy actions

Local actions run on every member without coordination; cell actions run on the policy leader (NATS KV lease
`policy-leader`).

| Action | Where | Trigger ([Anna-T] §5) | Effect |
| --- | --- | --- | --- |
| Promote | member | key accessed more than `P` times in `T` s | keep its state resident in the memory tier |
| Demote | member | fewer than `D` accesses in `T` s, or the memory share is full (CLOCK) | evict the clean state; it stays on disk |
| Hot-key replication | leader | frequency above mean + `s`·σ | raise the key's replication vector: memory replicas on more members first (network bandwidth, [Anna-T] §6.1), then more actors per member, up to all members and all actors |
| Cool down | leader | frequency below `L` | restore the default vector |
| Add member | leader | disk usage above `S_upper`, or latency above `f_upper`·`L_obj` with CPU above `C_upper` and hot-key replication exhausted | ask ts-orchestrator (TS-565 node pools) for a member, within the budget |
| Remove member | leader | latency below `f_lower`·`L_obj` and CPU below `C_lower` | drain one member; never below `max(rf)` members |

- **SLOs** ([Anna-T] §5): `policy-latency-ms` (`L_obj`), `policy-budget-per-hour` (budget cap `B`, in the currency
  ts-orchestrator prices node pools in) and the replication factor. Exactly one of latency or budget drives
  scaling; the budget is always a hard cap.
- **Grace period** after every membership change (`policy-grace-seconds`, 120 s): no demotions, replication changes
  or scaling while data moves. A per-hour cap on scale actions blunts short adversarial spikes ([Anna-T] §5.5).
- Node autoscaling calls ts-orchestrator only through its SDK (umbrella `AGENTS.md`, "Service ownership").
  Knobs and their defaults are documented with slice 11 and tuned with TS-606 runs.

## 11. Multi-region (later)

Built now: region and zone labels, region-aware identities and policies, interfaces. Built later: the WAN path.

### 11.1 Model

- Each region (cell) runs its own ring and membership in its own ts-orchestrator NATS. Regions do not share a ring.
- A database's replication policy names its home region and remote regions (`remote: [{"region","rf"}]`). Each
  region places its replicas on its own ring.
- **Local path** ([§7.2](#72-local-path-between-members-of-a-region)): low-latency push after every group commit,
  durable acks, write-wait. Built now.
- **WAN path**: asynchronous, batched and compressed shipping of delta-intervals between regions, tolerant of long
  partitions. Specified here, stubbed now.

### 11.2 WAN path

- Per database and remote region, one **gateway** member (the database's primary in each region) runs Algorithm 2
  at region granularity: the region is the replica, the gateway's database file is `X_r`, and a region-level
  interval sequence `c_r` numbers what the gateway ships. Received WAN intervals enter the region through the local
  path like any other delta.
- Batches every `wan-batch-ms` (1 s) or 4 MiB, LZ4-compressed in the same envelope. Acks are region-level and
  durable. A partition only grows the backlog; once the backlog passes its byte cap, the gateway falls back to a
  snapshot (a TS-633 snapshot in object storage, referenced, not streamed) plus the intervals after it.
- Transport: ts-orchestrator's NATS fabric between orchestrators (TS-562), with remote cells found through Atlas
  discovery. TerraBase gets a subject or stream per (database, region pair) from the orchestrator; nothing in this
  design opens WAN sockets of its own. This is the user's decision of 2026-10-07; `AGENTS.md` requires gRPC between
  TerraBase nodes, so a human edits that rule when slice 13 starts.

```csharp
// Engine/Anna, stubbed in slice 6: the local path calls them, the only implementation does nothing.
public interface IWanShipper
{
    ValueTask OfferAsync(DatabaseId database, long sequence, ReadOnlyMemory<byte> interval, CancellationToken cancellationToken);
}

public interface IWanReceiver
{
    IAsyncEnumerable<WanInterval> ReceiveAsync(DatabaseId database, RegionId from, long after, CancellationToken cancellationToken);
}
```

### 11.3 Consistency across regions

- **LWW** works across regions as it does inside one: HLC stamps, `max-clock-skew` refusal.
- **Causal**: dots already carry the region in `ReplicaId`, so contexts are dotted version vectors over
  `(region, member, actor)` and merge across regions with the same join. A region's context is causal inside the
  region first. A token from region A presented in region B is served once B's state covers it (WAN delivery),
  waiting at most `write-wait-ms`; otherwise B answers `409 not_covered` with the home region in
  `X-TerraBase-Region`, and the client retries there or later. Region pinning (`X-TerraScale-Region-Pin`,
  [region identity](../region-identity.md)) already keeps sessions in one region.
- **CAS and transactions** stay in the home region: owners exist only there; other regions forward owner operations
  to it or answer `421` with the home region, a choice for that slice.
- **Write-wait** covers the local path only; WAN acks are visible in metrics.

## 12. Queries, indexes, shapes and backups

- **Materialized rows.** Each disk replica's SQLite file keeps `documents(collection, id, state BLOB, body TEXT,
  deleted, conflict, stamp, …)`: `state` is the encoded register, `body` the LWW-resolved JSON (null when deleted),
  `conflict` set when several live versions exist. The flusher rewrites both in the same transaction as the delta.
- **Queries** (`queryDocuments`, `listDocuments`, counts, `assert_count`) and **SQL** (`runSql`, `checkSql`) compile
  as today against `body` on the local file. They see every write the member committed (acknowledged writes on this
  member, plus intervals it joined) and never half a transaction. Documents in conflict appear as their LWW winner.
  A non-replica member forwards the query to a disk replica.
- **Indexes** are SQLite expression indexes on `body`, created on each disk replica when the `_indexes` definition's
  winning state says so, as today. Index recommendations run on local plans, as today.
- **Shapes** (`getShape`) use the `intervals` table as their log: offsets are interval sequences of the serving
  member's copy, and the handle names that member and the copy's incarnation. Another member forwards the request
  to it; once that member is gone the handle answers `409 must_refetch`, which clients already handle.
- **Backups** (TS-633): the WAL shipper runs on one disk replica per database, holding its lease in NATS as TS-633
  designs it. A restore or branch writes a new database (new id, new incarnation) from the file; its replicas fill
  through full-state anti-entropy. Restoring over an existing database bumps its incarnation, so old tokens and
  shape handles stop matching.

## 13. API compatibility matrix

Routes as in [the route table](../api.md); `P`, `D`, `C` are its prefixes. "Same" means the behavior and the
guarantee do not change. Today's guarantees come from the current code and decisions
([0004](../decisions/0004-document-api-v1.md), [0010](../decisions/0010-write-wait-and-synced-durable-ack.md),
[replication](../replication.md)).

### Data

| Operation | Today | Anna core | Weaker or changed |
| --- | --- | --- | --- |
| `getDocument` `GET C/documents/{id}` | member's SQLite (or cache); read-your-writes across members after write-wait | owner or replica actor memory, else SQLite; same write-wait | Same under LWW. Causal adds `siblings`, `state:"conflict"`, `X-TerraBase-After` |
| `createDocument` `POST C/documents` | local commit, write-wait; generated UUIDv7 | owner-routed (generated id, `If-Match`/`If-None-Match`) | Same; one extra hop when the receiving member is not the primary |
| `putDocument` `PUT C/documents/{id}` | local serializable commit; cross-member LWW by commit offset | actor write, group commit; LWW by HLC | Unconditional: same semantics, HLC instead of offset. Conditional: now linearizable per key (stronger) |
| `patchDocument` `PATCH C/documents/{id}` | read-modify-write in a local transaction; `inc` exact per member | owner-routed read-modify-write | `inc` exact across members against other owner operations (stronger); a concurrent unconditional `PUT` elsewhere can still win under LWW |
| `deleteDocument` `DELETE C/documents/{id}` | tombstone, local commit | tombstone version, GC after every replica acked | Same |
| `listDocuments` `GET C/documents` | keyset pages from the member's SQLite | same, on a disk replica | Same; conflicted documents list as their LWW winner |
| `queryDocuments` `POST C/query` | one SQLite statement on the member | same, on a disk replica | Same |
| `runTransaction` `POST D/transactions` | serializable on one member; replicated as one group; cross-member LWW | read-atomic ([§9.4](#94-read-atomic-transactions)) | **Weaker**: not serializable on one member; `assert_count` can see phantoms. Per-key conditions stay atomic with the commit |
| `createCollection`, `listCollections`, `getCollection`, `dropCollection` | `_collections` documents; drop captures document deletions | same documents, owner-routed create and drop | Same; a drop of a populated collection writes its tombstones in one interval |
| `getShape` `GET C/shape` | snapshot and log by offset, `409 must_refetch` on a stale handle | log = intervals of the serving member's copy | Handle bound to a member; requests are forwarded to it, `409 must_refetch` once it left |
| `runSql`, `checkSql` `POST D/sql[/check]` | read-only SQL on the member's file | same, on a disk replica | Same; conflicts visible as LWW winners |
| Saved queries `P/saved-queries…` (5 operations) | catalog documents | `_system` documents | Same |

### Management and system

| Operation | Anna core | Weaker or changed |
| --- | --- | --- |
| `createDatabase` `POST P/databases` | registry document via owner `If-None-Match: *`, placement on the ring | Same; no catalog primary forwarding |
| `listDatabases`, `getDatabase` | local `_system` replica | Same; may lag a create on another member by the local-path delay unless write-wait completed |
| `deleteDatabase` | registry tombstone, then each replica deletes its file; delete GC per [§7.4](#74-garbage-collection) | Same |
| `branchDatabase` | copy of a replica's file under a new id, filled to the new replicas by full state | Same |
| `createBackup`, `listBackups`, `restoreBackup`, `getBackupPolicy`, `setBackupPolicy` (and TS-588's `downloadBackup`) | TS-633 shipper on one disk replica; policy in `_system` | Same; restore over an existing database invalidates its tokens and shape handles |
| `createIndex`, `listIndexes`, `dropIndex`, `listIndexRecommendations`, `applyIndexRecommendation` | `_indexes` documents; SQLite indexes on each disk replica | Same |
| `getDatabaseSchema`, `getCollectionSchema` | from a disk replica | Same |
| `getProjectUsage`, `getDatabaseUsage` (TS-581) | `GCounter`s per `(database, hour)` | Same totals once intervals converge; no lost increments under concurrency |
| `healthz`, `readyz` | `readyz` also requires membership joined (state `active`) when `core=anna` | Readiness waits for the join |
| `/`, `/scalar/`, `/openapi/v1.json`, `/metrics`, `/admin/node/pressure` | unchanged; new metrics added | Same |

### SDK calls

The SDK clients (ts-dotnet `NativeClient`, ts-go, ts-js `TerraBaseClient`) speak the native contract under
`/v1/tenants/{tenant}/databases/{database}/shards/{shard}/…`, which this server does not serve. Their operations map
to the routes above as follows, so the guarantees in the Data table apply once they call those routes:

| SDK operation | Route |
| --- | --- |
| `read` / `ReadAsync` | `getDocument` |
| `query` / `QueryAsync` | `queryDocuments` |
| `subscription` (`subscribeDocuments`) | `getShape` |
| `commit` / `CommitAsync` | `runTransaction` |
| `transactionStatus` / `TransactionStatusAsync` | none today; a TxId lookup can back it later |

ts-cli pins the same contract. The dashboard calls the Management and Data routes and keeps working.

### Headers and members added

`X-TerraBase-Consistency` (request), `X-TerraBase-After` (request), `siblings` and `deleted` (response members of
causal reads), `tbv2-` version tokens. All additive; a client that sends none of them gets LWW behavior.

## 14. Failure modes

| Failure | Effect | Handling |
| --- | --- | --- |
| Process crash | memory tier and pending writes lost | pending writes were never acknowledged; restart reloads from SQLite; dots never reused ([§6](#6-membership-and-gossip)) |
| OS crash or power loss | the last `NORMAL` commits may be lost locally | as today ([0003](../decisions/0003-sqlite-synchronous-normal.md)); peers that sent a `DurableAck` (FULL) still hold them, write-wait waited for them |
| Disk lost | member's databases gone | re-registers with a new member number, receives full state from the other replicas |
| Member partitioned from peers, not NATS | writes continue on both sides; intervals pile up | anti-entropy resumes; bounded backlog falls back to full state; write-wait times out and counts |
| Member partitioned from NATS | its lease expires | it stops serving owner operations (CAS, patches, conditional transactions answer `503`); LWW and causal writes go on |
| NATS down | ring frozen, no joins, leaves or leases | data path continues; owner operations stop when leases expire; autoscaling pauses. CAS availability depends on NATS |
| Clock skew beyond `max-clock-skew` | LWW anomalies, unsafe leases | remote stamps refused and counted; leases and reservations assume drift below the lease margin (stated, not proved) |
| Slow or lagging replica | acks lag | write-wait bounded as today; `terrabase_replication_*` lag metrics; full state past the retained window |
| Hot key overloads its actor | inbox fills | `503` with `Retry-After` until the policy engine replicates the key |
| Memory tier full | admissions fail | CLOCK eviction of clean keys; writes answer `503` while pending bytes exceed the share |
| Disk full or flush error | batch fails | the batch's writes answer `503` and never take effect |
| Coordinator dies mid-transaction | reservations held | they expire at the deadline; no late commit |
| Primary changes during CAS | two members could act as owner | the new primary waits out the old lease and catches up first |
| Ring views disagree briefly | misrouted requests | forwarded to a current replica; epochs compared on every peer frame |

## 15. Test plan

Both suites stay parallel-safe: each test starts its own servers on ephemeral ports with its own data directories
and synthetic data (Bogus), and never uses `[NotInParallel]`.

1. **Lattice laws (slice 1, Fast).** Property tests over generated states, seeds printed on failure, for every
   lattice: associativity, commutativity, idempotence, `⊥` identity, `⊑` consistent with join; delta-mutators
   inflate (`X ⊑ X ⊔ mδ(X)`) and decompose the mutator (`m(X) = X ⊔ mδ(X)`); replicas that receive the same deltas
   in any order, with duplicates, converge ([δ-CRDT] Prop. 1); `MvRegister` keeps concurrent writes and replaces
   observed ones; `CausalContext` compaction preserves membership. **Rule conflict**: these tests call the library
   directly, and `AGENTS.md` allows integration tests only. TS-634's scope (user, 2026-10-06) asks for property
   tests of the lattice laws; a human must confirm the exception or say how to run them instead
   ([§17](#17-open-questions-for-humans)).
2. **Engine behind the switch (Fast).** The existing Fast suite runs against `core=anna` as a second server mode:
   every document, collection, query, transaction and shape test must pass in both modes, except the guarantees the
   matrix changes, which get mode-specific tests.
3. **Multi-member (Fast, small).** Three in-process members with the static `peers` membership: write on one, read
   on the others after write-wait; concurrent LWW writes converge; causal siblings appear and resolve; CAS on two
   members, exactly one wins.
4. **Partition suite, Jepsen-style (Slow).** Three to five in-process members whose peer links go through a fault
   proxy (drop, delay, duplicate, reorder, partition, heal) under a NATS server started by the test (or the static
   membership where NATS is not needed). Histories of client operations are recorded and checked:
   - convergence: after heal and quiescence, every replica's state is equal;
   - no lost acknowledged writes: every acknowledged LWW write is readable or superseded by a greater stamp;
   - causal: no read returns a version without its causal past, given `X-TerraBase-After`;
   - CAS: conditional writes of a key are linearizable (Knossos-style check on single-key histories);
   - transactions: no fractured reads and all-or-nothing visibility.
   Slow failures open a Forgejo issue with the seed and history, as the Slow suite requires.
5. **Crash and restart (Slow).** Kill a member mid-flush; restart; check acknowledged writes survive and dots are
   not reused. Lose a member's data directory; check it re-registers and refills.

## 16. Slice plan

Each slice is one pull request, merged on green CI. Merges to `main` deploy to production, so every slice keeps
`core=sqlite` as the default and adds no behavior to the default path; `core=anna` is reachable only by setting it.
Production stays on `core=sqlite` until slice 12.

| # | Slice | Contents | Shippable because |
| --- | --- | --- | --- |
| 0 | Design | this document | docs only |
| 1 | Lattice library | `Engine/Lattices`: `ILattice`, `MaxLattice`, `Hlc`, `LwwRegister`, `SetLattice`, `MapLattice`, `Dot`, `CausalContext`, `MvRegister`, `AwSet`, `GCounter`; lattice-law property tests | no caller yet |
| 2 | Switch and actors | `core` setting; actors, inboxes, router, local ring; point reads and writes in memory on one node; Fast suite runs both modes for point operations | `core=anna` starts only in Development and without `peer-port`; writes answer `"durability":"memory"`; lists, queries, transactions and shapes answer `503` |
| 3 | Disk tier | `IDiskTier` on SQLite: new schema (`user_version`), encoding, flushers with group commit, pending/committed, cold loads, materialized rows; lists, queries, SQL, indexes | `core=anna` becomes usable on a single node |
| 4 | Consistency per request | HLC, LWW and causal reads and writes, `tbv2-` tokens, `siblings`, `X-TerraBase-After` | additive headers and members |
| 5 | Membership and ring | `IMembership`: static `peers` and NATS KV (region and zone labels, heartbeats, member numbers, leases); placement per database; forwarding | NATS used only when `core=anna` and `nats-url` are set |
| 6 | Local-path anti-entropy | intervals, acks, full-state catch-up, write-wait on acks, `IWanShipper`/`IWanReceiver` stubs | multi-member `core=anna` |
| 7 | Keyed CAS | owners, `If-Match`/`If-None-Match`/`PATCH`/generated ids through owners, failover with leases | |
| 8 | Read-atomic transactions | TxIds, prepares and reservations, single-interval commits, in-flight table | |
| 9 | Catalog, shapes, backups | `_system` as lattices (no catalog primary), shapes on intervals, TS-633 shipper and restore hooks, usage `GCounter`s | |
| 10 | Policy engine | monitoring, promotion and demotion, hot-key replication | local actions first, leader actions behind `policy=on` |
| 11 | Autoscaling | ts-orchestrator node pools through its SDK, budget cap, grace periods | off unless a budget is set |
| 12 | Qualify and cut over | Partition suite green; TS-606 run with `core=anna` meets [§18](#18-benchmark-targets); at the TS-602 wipe set `core=anna` as the default, then delete the old core and its catalog primary | human go/no-go |
| 13 | Multi-region | WAN path over TS-562's NATS fabric and Atlas, remote replicas in the replication policy, cross-region causal tokens | later, after a region pair exists |

Slices 2 to 4 are single-node and can be qualified with the TS-500 local harness before membership exists.

## 17. Open questions for humans

1. **Property tests and the integration-only rule** ([§15](#15-test-plan)): allow lattice-law property tests that call
   `Engine/Lattices` directly in the Fast suite? Slice 1 ships them as asked by TS-634 unless told otherwise.
2. **Actor pinning** needs `AllowUnsafeBlocks` for `LibraryImport`. Allowed in slice 2, or start unpinned?
3. **New dependencies**: `System.IO.Hashing` (XxHash3, Microsoft) for the rings in slice 2 and `NATS.Net` (the
   official NATS client) for membership in slice 5. Both are maintained, popular and Newtonsoft-free.
4. **CAS availability**: owner operations stop when NATS is unreachable long enough for leases to expire. Accept,
   or keep a static-membership fallback for single-cell deployments?
5. **Transactions**: is read-atomic (with per-key conditions atomic with the commit, `assert_count` not reserved)
   acceptable as the documented weaker isolation?

## 18. Benchmark targets

### Today

TS-606 has not run load against gru-1 yet: its first run (`ts-benchmark` `bench/deployed/2026-10-06`) stopped at
preflight. It recorded beta → `gru.api` through Cloudflare at a median `/healthz` of 73.1 ms and idle members at
2-6 % CPU and 194-236 MB peak RSS. The latest load numbers for the C# server are TS-500's local acceptance baseline
(`ts-benchmark` `bench/csharp-baseline/ts500-20261004*`, one node, one release, three runs); concurrency 16:

| Workload | ops/s | p99 ms |
| --- | ---: | ---: |
| point_read | 10,111-13,710 | 4.4-5.7 |
| point_write | 2,012-2,268 | 54.9-79.2 |
| query_filter_sort | 519-3,188 | 12.6-55.0 |
| transactions | 2,182-2,463 | 54.6-80.7 |
| mixed_90_10 | 8,562-15,686 | 6.0-7.9 |

Writes plateau at about 2,000-2,300 ops/s with p99 above 50 ms: one SQLite write transaction per client write,
serialized per database. That is the bottleneck this design attacks.

### Targets for `core=anna`

Measured with the same harness and hardware as the baseline, then with TS-606 on gru-1. Targets, not results:

| Workload (concurrency 16) | Target | Versus today |
| --- | --- | --- |
| point_write | ≥ 10,000 ops/s, p99 ≤ 10 ms | about 5×, p99 5× lower (group commit) |
| transactions (unconditional) | ≥ 5,000 ops/s, p99 ≤ 20 ms | about 2× |
| point_read | ≥ 13,000 ops/s, p99 ≤ 5 ms | no regression; hot keys from memory |
| query_filter_sort | within 10 % of today | same SQLite path |
| mixed_90_10 | ≥ 15,000 ops/s | no regression, higher on write-heavy mixes |
| Zipf 2 single hot key, writes | ≥ 3× one actor's throughput with hot-key replication | new capability ([Anna] Fig. 8a) |
| Write scaling, uniform keys, 1 → N actors | ≥ 0.7 × linear | [Anna] Fig. 4b |

On gru-1 through Cloudflare (TS-606): point operations at p50 within the measured `/healthz` round trip + 5 ms,
throughput at saturation at least 3× and cost per million operations at most a third of the `core=sqlite` numbers
TS-606 records first, with no errors in final readback. Slice 12 cuts over only when these hold.
