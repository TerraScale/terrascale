# Anna core: coordination-free storage and replication (TS-634)

- Status: design, revised 2026-10-07 for TS-647. **Stage 1 (TS-647) is being built now; stage 2 (TS-648, TS-649,
  TS-643) comes later.** Nothing below is implemented unless the [plan](#15-plan) marks its step as merged.
  Statements about the future system are targets, not guarantees; each guarantee is claimed only once its step ships
  with integration evidence.
- Tickets: TS-634 (umbrella), TS-647 (stage 1), TS-648 (per-core actors), TS-649 (memory tier, hot keys, policy
  engine, autoscaling), TS-643 (multi-region), TS-650 (SDKs accept `tbv2` tokens, no longer needed: decision 7), TS-677 (go-live).
- User decisions: 2026-10-06 (scope), 2026-10-07 (replicate inside one region now; one engine, no core switch; the
  simpler route now and full Anna later; [§16](#16-decisions-and-open-questions)).
- Related: TS-602 (greenfield wipe, no longer the cutover: decision 6), TS-606 (deployed benchmark), TS-633 (continuous backup),
  TS-565 (node pools), TS-562 (NATS fabric between orchestrators), TS-581 (usage metering).

Sources:

1. **[Anna]** C. Wu, J. M. Faleiro, Y. Lin, J. M. Hellerstein, *Anna: A KVS for Any Scale*, ICDE 2018.
   <https://dsf.berkeley.edu/jmh/papers/anna_ieee18.pdf>
2. **[Anna-T]** C. Wu, V. Sreekanti, J. M. Hellerstein, *Autoscaling Tiered Cloud Storage in Anna*, PVLDB 12(6), 2019.
   <https://www.vldb.org/pvldb/vol12/p624-wu.pdf>
3. **[δ-CRDT]** P. S. Almeida, A. Shoker, C. Baquero, *Delta State Replicated Data Types*, arXiv:1603.01529.
   <https://arxiv.org/abs/1603.01529>
4. **[AFT]** V. Sreekanti et al., *A Fault-Tolerance Shim for Serverless Computing*, EuroSys 2020.

## 1. Summary

TerraBase has **one engine**: Anna's leaderless replication, with SQLite (Microsoft.Data.Sqlite, bundled) as its
on-disk storage. There is no engine switch.

Anna has two halves:

- **Replication**: lattice values, delta anti-entropy, membership, ring and leases. This is sound at every scale and
  is stage 1.
- **Execution**: per-core actors with a memory tier as the source of truth. On today's production nodes (A1, 1 OCPU,
  4 GB) one core means one actor, which is a single global queue plus thread switches, with no parallelism to gain.
  This is stage 2, for larger nodes.

**Stage 1 (TS-647)** evolves today's SQLite engine and its per-origin replication streams in place:

- every document is an `MvRegister` lattice with hybrid-logical-clock stamps, and replicas **join** states instead
  of applying merge policy v1;
- a per-database **group-commit writer** on its own thread commits many client writes per SQLite transaction (one
  `SAVEPOINT` each), separate from the read pool;
- each member streams its own writes per database (**per-origin streams**) with **dependency vectors**, giving
  causal delivery at O(rf) without transitive re-sends;
- **NATS** (ts-orchestrator's embedded JetStream KV) holds membership, the ring and leases; static `peers` stays for
  tests and single-node runs;
- the primary **owner** of each database serves compare-and-set and conditional transactions; everything else is
  accepted by any replica;
- `_system` becomes a lattice database on every member, and the catalog primary goes.

**Stage 2** adds per-core actors (TS-648), a memory tier with hot-key replication, a policy engine and autoscaling
(TS-649), and replication across regions (TS-643). Stage 1 code marks every deliberate simplification with
`// anna-full (TS-648|TS-649|TS-643): <what full Anna does here>` so stage 2 can find and remove them.

The public HTTP API, collections, SDK calls and the dashboard keep working; the
[compatibility matrix](#12-api-compatibility-matrix) lists what changes. Production keeps its data: a one-time
in-place migration converts each database file when its member first runs the new format
([§16](#16-decisions-and-open-questions), decision 6).

### Non-goals

- A TerraBase-owned B-tree or LSM engine (decision [0002](../decisions/0002-sqlite-storage-engine.md)).
- Linearizable reads or serializable transactions across members. Owner compare-and-set is the one coordinated
  operation.
- Turso or any second storage engine.

## 2. Architecture

```text
            client (HTTPS, ts-auth JWT)
                      |
  +-------------------v------------------------------------------- member (region gru, zone ad-1) --+
  |  Features/*: endpoints, validation                                                              |
  |  Features/Documents: one DocumentService, ShapeService, ETags                                   |
  |        | not a replica, or an owner operation on another member -> forward (Engine/Cluster)     |
  |        v                                                                                        |
  |  Engine/Sqlite: per-database writer thread (group commit, one SAVEPOINT per client write),      |
  |                 read pool, join-on-apply, queries, indexes, WAL tap                             |
  |        | committed groups                                                                       |
  |        v                                                                                        |
  |  Engine/Replication: per-origin streams, dependency vectors, durable acks, write-wait, GC  <--> peers (mTLS gRPC)
  |  Engine/Cluster: membership (NATS KV or static peers), ring, leases, owner routing               |
  |  Features/Backup: one WAL shipper per database, by lease                                        |
  +-------------------------------------------------------------------------------------------------+
```

### Layering

Dependencies point down only:

| Layer | Holds |
| --- | --- |
| `Features/*` | Endpoints and request validation |
| `Features/Documents` | One `DocumentService`, `ShapeService`, ETags and version tokens |
| `Engine/Lattices` | Pure values and joins (merged: TS-634 slice 1) |
| `Engine/Replication` | Intervals, dependency vectors, acks, write-wait, GC, the peer gRPC service |
| `Engine/Cluster` | Membership (NATS or static), ring, leases, owner routing, forwarding |
| `Engine/Sqlite` | Schema, encoding, the group-commit writer, join-on-apply, queries, indexes, WAL tap |
| `Features/Backup` | WAL shipping and restores, driven by the cluster's leases |

Everything the old single-writer model needed and stage 1 does not (`VersionClock`, `StampingStorageEngine` and
`IWriteStamps`, `JournalDigest`, merge policy v1, baselines, legacy schema upgrades, the catalog primary) is deleted,
not adapted.

## 3. Lattice library

`Engine/Lattices` holds immutable join-semilattices: a bottom `⊥` and a join `⊔` that is associative, commutative and
idempotent ([Anna] §III). Mutators are delta-mutators: they return the delta, and the new state is `X ⊔ mδ(X)`
([δ-CRDT] Def. 1).

| Type | Join | Used for |
| --- | --- | --- |
| `MaxLattice` | max | sequence numbers, ack positions |
| `Hlc` + `LwwRegister<T>` | greater `(Hlc, ReplicaId)` | membership and policy metadata |
| `SetLattice<T>`, `MapLattice<TKey, TValue>` | union; union of keys and join of values | groups, intervals |
| `Dot`, `CausalContext` | union, compacted to a version vector plus a cloud | causal metadata |
| `MvRegister<T>` | `Causal<DotFun>` ([δ-CRDT] Fig. 12, 14) | **document state** |
| `AwSet<T>` | `Causal<DotMap<T, DotSet>>` | add-wins sets |
| `GCounter` | pointwise max | usage counters (TS-581) |

### Identities and dots

- `ReplicaId` packs region (16 bits), member (32 bits) and **epoch** (16 bits). The member number is drawn at random
  when a data directory is first used (decision 8); the region keeps dots distinct across regions later and is 0 until
  then. Both live in `.terrabase.replica` in the data directory (step 4b): a format byte, the member number, the epoch
  and whether the last process stopped cleanly.
- **The epoch rises on every unclean start** (the file does not say the last process stopped cleanly), and is written
  through to disk before the storage engine starts, so before the member can mint a dot. Past epoch 65535 the member
  takes a new number. A clean stop is recorded only once every store closed without error. With `synchronous = NORMAL`
  ([0003](../decisions/0003-sqlite-synchronous-normal.md)) an OS crash can lose commits a peer already received;
  minting from the recovered state with the same replica id would reuse their dots and silently drop one write
  everywhere (the Riak kv679 class of bug). A new epoch is a new replica, so it cannot.
- **New keys start from the database's floor**: the version vector of dots collected by tombstone GC
  ([§6.4](#64-garbage-collection)). `new MvRegister<T>(floor)` mints above it, so a key recreated after its tombstone
  was collected never reuses a dot another replica still covers.
- **A failed write burns its dot**: the writer joins the dot into the key's context (`new MvRegister<T>(context of
  the dot)`), so the next write mints a new one.
- **A dot carrying two values is refused**: `MvRegister.Join` throws instead of keeping one. Any remaining reuse bug
  then stops a stream loudly instead of losing data.
- `Hlc`: 48-bit Unix milliseconds and a 16-bit logical counter. A received stamp ahead of the local clock by more
  than `max-clock-skew` (default 1 s) is **accepted**, the local clock advances by at most `max-clock-skew`, and an
  alert metric counts it; the stream never stalls on a fast clock.

### Documents

A document is one `MvRegister<DocumentWrite>` keyed by `(database, collection, id)`:

```text
DocumentWrite = (Hlc stamp, Body)        Body = UTF-8 JSON object bytes | Tombstone
```

- **LWW write** (the default): replaces every version the writing replica knows. **Causal write** (step 7): replaces
  the versions in the client's token only.
- **LWW read**: the version with the greatest `(Hlc, ReplicaId)`; a tombstone winner reads as `404`.
- Deletes write a tombstone version, so a LWW delete beats older concurrent writes; tombstones go once every replica
  applied them ([§6.4](#64-garbage-collection)).
- The whole document is one register: PUT replaces whole documents and queries need one materialized JSON per
  document. A JSON CRDT per field is possible with the same library and not planned.

### Encoding

Persisted and sent states use one versioned encoding, independent of .NET layout and endianness
(`Engine/Lattices/LatticeEncoding.cs`, step 4a): a format byte, little-endian fixed-width integers, minimal LEB128
counts and lengths, replicas and dots sorted by `(replica, counter)`, so equal states encode to equal bytes. A register
is its format byte, its context's prefixes `(replica, counter)` and cloud dots, then its writes `(dot, stamp, kind,
[length, body])`. Decoding is a trust boundary: it accepts canonical bytes only (ascending, no duplicates, non-zero
prefixes, a cloud that neither lies within nor extends its prefix, every write inside the context, minimal lengths, no
trailing bytes), and bounds every count by the bytes left, so decoding allocates in proportion to its input. Dots and
replica ids hash both halves of their 64-bit values, so crafted values cannot collide into quadratic lookups.

## 4. Writes on SQLite

### 4.1 Group commit

- One **writer thread per database** owns the database's write connection; reads use the existing read pool, so a
  long `runSql` no longer blocks writes (`storage-threads` resolves to 1 on the 1-OCPU nodes today).
- The writer drains its queue into one SQLite transaction per batch. Each client write runs in its **own
  `SAVEPOINT`**: a write that fails (precondition, admission, limits) rolls back alone, and the batch commits the
  rest. A write answers after its batch committed (`"durability":"local_durable"`, `synchronous = NORMAL` as today),
  then after write-wait ([§6.3](#63-acks-and-write-wait)).
- Batches are bounded by writes and bytes (a storage transaction captures at most 1024 changes and 2 MiB).
- Point operations look collections up in an in-memory set per database, kept by the writer, instead of opening an
  SQLite read transaction. Data requests resolve their database from the catalog through a cache that stays valid
  while the catalog has not committed since the lookup, so a busy read pool holds up no write (step 5 replaces the
  catalog).
- Writes of one batch share its commit offset, as the operations of one transaction always did.
- Join-on-apply: a replicated group is applied by the same writer, joining each incoming register into the stored
  one.
- `// anna-full (TS-648)`: per-core actors take writes in parallel and hand batches to the writer.
- `// anna-full (TS-649)`: a memory tier serves hot keys without SQLite reads.

Target: about 5× today's write throughput, measured with the TS-606 harness on 1-OCPU A1 nodes, with equal or lower
CPU and memory ([§17](#17-benchmark-targets)).

### 4.2 Stored state

Each document row keeps the encoded register, the LWW-resolved JSON (what queries, indexes, shapes and SQL read), its
version token and whether siblings exist. The database keeps its floor vector, its origin streams' positions and the
applied vector. One file per database stays, so TS-633's WAL tap keeps working unchanged.

## 5. Membership, ring and leases

### 5.1 Membership

- Production: ts-orchestrator's embedded NATS (v2.15.0) JetStream KV, in the bucket ts-orchestrator already grants
  TerraBase (`terrabase_backup_leases`, memory storage); a dedicated bucket needs a grant there first. One key per
  member (`member.<base64url node-name>`) with the member's region, zone and peer address. Members
  rewrite their key every **5 s** (the heartbeat) and read the others in the same refresh; the bucket's `max_age` of
  **15 s** with **subject delete markers** (`LimitMarkerTTL`) expires a member nobody rewrites. Reads are pulls, not
  watches, so every member decides from a view it read itself (`Engine/Cluster/StoreMembership.cs`).
- A member that shuts down cleanly deletes its key after continuous backup stopped, so the others take its databases
  over at once.
- Tests and single-node runs: the static `peers` setting, a fixed member list whose leases never end.
- `peers` and NATS membership replace `peer-addresses`.
- The member number lives in the data directory ([§3](#identities-and-dots)); a member that lost its data directory
  starts again under a new number.

### 5.2 Ring and placement

- Consistent hashing with virtual nodes per region ([Anna-T] §4.2): each member owns `64 × weight` tokens. A
  database's replicas are the first `rf` distinct members clockwise from the hash of its id, distinct zones first.
  Default `rf = min(3, members)`: a 3-member cell keeps every database on every member, as today.
- The **primary** is the first replica. It owns the database's owner operations ([§7.2](#72-owner-operations)).
- A request that reaches a member that does not hold the database is forwarded to a replica over the peer link.
- `// anna-full (TS-649)`: hot keys get extra memory replicas through per-key replication vectors.

### 5.3 Leases

- A member's KV key is its lease. A member holds it while its last successful renewal (its write, then its read of the
  others) began less than the **fence** ago by its own monotonic clock: fence = TTL − heartbeat = **10 s**, so it
  stops a heartbeat before its key can expire for the others, and never acts on a view older than the fence.
- A member that becomes a database's primary starts at once when the previous primary left its view (that primary's
  key expired or was deleted, so it had already stopped); otherwise it waits out the fence, by which time the previous
  primary has either read the new ring or lost its lease. The term's epoch is the membership revision it began at.
- Step 3 uses this for the backup shipper (`IShipperLeases`, TS-633): each database is shipped by its primary.
  Owner operations use the same rule in step 6.
- **A member whose lease expired drops its replicas and refills them from peers** before it serves again, so a
  member that missed deletions while it was away cannot bring deleted documents back.
- **GC pauses and the claim.** A paused process can outlive its lease. The owner therefore checks its lease before
  it validates a condition and again after its batch committed, and answers `503` (outcome unknown) when the lease
  lapsed in between. Even so, a stale owner's committed write can still reach the replicas and join as a concurrent
  last-writer-wins write. The documented claim is therefore: **acknowledged owner operations of a key are
  linearizable with each other as long as pauses and clock drift stay below the lease margin**; beyond it, an
  unacknowledged owner write can take effect as a concurrent write. Fencing writes by lease revision would remove
  this caveat but needs every replica to reject joins, which lattices do not do; it is not planned.
- When NATS is unreachable long enough for leases to expire, owner operations answer `503`; plain reads and writes
  continue (user decision 2026-10-07).

## 6. Replication

### 6.1 Per-origin streams

- Every member keeps, per database, the stream of groups it **originated** (its own committed client writes), each
  with a sequence number. Peers receive each origin's stream directly from that origin; a member never re-sends
  another member's groups. Traffic is O(rf) per write.
- Each group carries a **dependency vector**: for every other origin, the last sequence the writing member had
  applied when it committed. A receiver applies a group only once its applied vector covers the group's dependencies,
  so every replica sees writes in causal order without transitive re-sends (the dependency vector replaces the
  transitive mode of [δ-CRDT] Algorithm 2, with the same causal delivery guarantee).
- A replica that falls behind the retained stream, or a new or refilled replica, receives a snapshot of the encoded
  states and the applied vector, then the streams after it.
- Wire format: the existing versioned envelope with LZ4 over the existing mutual-TLS peer listener
  ([0007](../decisions/0007-lz4-application-envelope-replication.md)).

### 6.2 Join on apply

The writer joins each incoming register into the stored one, so delivery order, duplicates and retries cannot change
the result ([Anna] §III). Merge policy v1 and the journal digests go.

### 6.3 Acks and write-wait

A receiver acknowledges an origin's sequence after it applied the group and synced it (`synchronous = FULL`,
[0010](../decisions/0010-write-wait-and-synced-durable-ack.md)). Write-wait keeps its meaning: a client write answers
after every active replica of its database acknowledged it, bounded by `write-wait-ms`.

### 6.4 Garbage collection

- A group leaves its origin's retained stream once every replica acknowledged it and the shape retention window
  passed.
- A tombstone goes once every replica's applied vector covers its dot; its dots join the database's **floor vector**,
  which seeds new keys ([§3](#identities-and-dots)).

## 7. Consistency, owner operations and transactions

### 7.1 Consistency per request (step 7)

`X-TerraBase-Consistency: lww | causal` (default `lww`).

- **LWW**: today's semantics, with HLC stamps instead of commit offsets.
- **Causal**: reads return every live version (`"state":"conflict"` and `siblings` when there are several, with the
  LWW winner in `document`); writes replace the versions in the client's token; `X-TerraBase-After: <token>` makes a
  read wait (at most `write-wait-ms`) until the serving replica covers the token, else forward to the primary.

### 7.2 Owner operations

- `If-Match`, `If-None-Match: *`, assertions (`assert`, `assert_count`) and conditional transactions run on the
  database's **primary owner**. The owner holds local reservations on the keys it checked until the flush callback of
  the batch that carries the write releases them, so these keep today's semantics: a conditional transaction is
  serializable against every other owner operation, as on today's single writer, and `ts-auth` and the dashboard keep
  their stores' guarantees.
- Creates with server-generated ULID ids and unconditional `PUT`, `PATCH` and `DELETE` need no owner: any replica
  accepts them and they join as last-writer-wins.
- An owner operation that reaches another member is forwarded to the primary.

### 7.3 Transactions

- A transaction with any condition is an owner operation ([§7.2](#72-owner-operations)).
- An unconditional transaction runs on any replica and commits as one group; every replica applies a group in one
  SQLite transaction, so no query, list or SQL read sees half of it.
- `// anna-full (TS-648)`: with per-core actors, conditional transactions need the [AFT] read-atomic protocol across
  actors instead of one writer.

## 8. Catalog (`_system`)

`_system` (database registry, backup policies, saved queries, usage counters) becomes a lattice database replicated
to every member like any other. The catalog primary and followers ([0009](../decisions/0009-catalog-replication.md))
go: `CatalogFollower`, `CatalogFeed`, `CatalogPrimaryClient`, `catalog.proto` and `ISystemReplication`. A unique
database name per project is an owner `If-None-Match: *`. Usage counts from every member become `GCounter`s.

## 9. Queries, indexes, shapes and backups

- Queries, lists, counts and SQL read the LWW-resolved JSON on the serving replica's file and see whole groups only.
- Indexes are SQLite expression indexes on each replica, created from the `_indexes` documents as today.
- Shapes read the serving member's per-database log; a handle names that member. Another member forwards to it; once
  it left, the handle answers `409 must_refetch`.
- **Backups (TS-633)**: one WAL shipper per database, chosen by a lease tied to the database's primary member lease.
  Engine/Cluster owns the lease; Features/Backup consumes it. When the lease moves, the new shipper starts a new
  backup generation with a snapshot.

## 10. Stage 2 (later)

- **TS-648, per-core actors** ([Anna] §IV-V): one actor per core with a private map of document states, hash
  partitioning across cores, merge-at-sender gossip between actors of a hot key, `ReplicaId` per actor. Worth it on
  multi-core nodes; on 1 OCPU it is a single queue.
- **TS-649, memory tier and policy** ([Anna-T] §3-5): hot keys in memory, selective replication across members and
  cores, promotion and demotion against the SQLite tier, node autoscaling through ts-orchestrator (TS-565) within a
  budget cap.
- **TS-643, multi-region**: each region keeps its ring and membership. A WAN path ships origin streams between
  regions in batches, asynchronously, tolerant of long partitions, over ts-orchestrator's NATS fabric (TS-562) with
  regions found through Atlas. Dots already carry the region, so causal contexts merge across regions; causal reads
  stay per region until a region covers a token. Owner operations stay in the database's home region.
  Stage 1 of it runs on today's engine: every member pulls each remote origin's groups over NATS request/reply
  ([cross-region replication](../cross-region.md)).

## 11. Failure modes (stage 1)

| Failure | Effect | Handling |
| --- | --- | --- |
| Process crash | unacknowledged writes in the writer's batch lost | acknowledged writes are committed; restart is clean or bumps the epoch |
| OS crash or power loss | the last `NORMAL` commits may be lost locally | peers that acknowledged (FULL) keep them; the epoch bump prevents dot reuse |
| Disk lost | the member's databases are gone | it registers under a new member number and refills from peers |
| Member partitioned from peers | writes continue on both sides | streams resume; past the retained window, snapshots; write-wait times out and counts |
| Member partitioned from NATS | its lease expires | owner operations answer `503`; after the partition it drops its replicas and refills |
| NATS down | membership and ring frozen | data path continues; owner operations stop once leases expire |
| Clock ahead by more than `max-clock-skew` | LWW favors the fast clock | the stamp is accepted, the local clock advances by at most `max-clock-skew`, an alert counts it |
| Owner paused past its lease | two owners briefly | lease checks before and after commit; the documented claim ([§5.3](#53-leases)) |
| A dot minted twice | two values under one dot | the join refuses it and the stream stops with an error |

## 12. API compatibility matrix

Routes as in [the route table](../api.md); `C` and `D` are its prefixes. Today's guarantees come from the current code
and decisions ([0004](../decisions/0004-document-api-v1.md), [0010](../decisions/0010-write-wait-and-synced-durable-ack.md),
[replication](../replication.md)).

| Operation | Today | Stage 1 | Weaker or changed |
| --- | --- | --- | --- |
| `getDocument` | member's SQLite; read-your-writes across members after write-wait | same | Same under LWW; causal reads add `siblings` (step 7) |
| `createDocument` (generated id) | local commit, write-wait | any replica | Same |
| `putDocument`, `deleteDocument` unconditional | local commit; cross-member LWW by commit offset | any replica; LWW by HLC | Same semantics, HLC instead of offsets |
| `putDocument`, `deleteDocument` with `If-Match` or `If-None-Match` | checked in each member's own transaction; two members can both accept | on the primary owner | **Stronger**: linearizable per key under the lease claim ([§5.3](#53-leases)) |
| `patchDocument` | read-modify-write on the member | any replica unconditionally; owner with `If-Match` | Same; an unconditional patch on two members concurrently keeps the later one, as today |
| `listDocuments`, `queryDocuments`, `runSql`, `checkSql` | the member's SQLite | the replica's SQLite | Same; documents with siblings show their LWW winner |
| `runTransaction` with conditions | serializable on one member; cross-member LWW | on the primary owner with reservations | Same semantics; **stronger** across members |
| `runTransaction` without conditions | as above | any replica, one group | Same atomicity; no cross-member serializability, as today |
| Collections create, list, get, drop | `_collections` documents | same, create and drop through the owner | Same |
| `getShape` | log by offset; `409 must_refetch` on a stale handle | handle names the serving member; forwarded | Same for clients that follow `409` |
| Databases, backups, indexes, schema, saved queries | catalog on the primary, forwarded from followers | `_system` lattice database on every member | Same; no catalog primary |
| Usage (TS-581) | follower counts forwarded | `GCounter`s | Same totals once replicas converge |
| `healthz`, `readyz`, `/metrics`, API reference | unchanged | `readyz` waits until membership joined | Same |

Version tokens keep their `tbv1-<32 hex>` shape: the hash then covers the register's winning write instead of the
commit offset. They stay opaque to clients, so SDKs need no change and no new versioned name appears (decision 7).
SDK operations map
to these routes as before: `read` → `getDocument`, `query` → `queryDocuments`, `subscription` → `getShape`, `commit`
→ `runTransaction`.

## 13. Test plan

Both suites stay parallel-safe: each test owns its servers, ports, directories and Bogus data.

1. **Lattice laws (Fast, merged).** Property tests of every lattice: ACI, bottom, inflation, convergence under
   reordering and duplication, `MvRegister` sibling semantics, add-wins, canonical contexts, HLC monotonicity, and the
   dot rules: a dot minted twice is refused, a new epoch never reuses a dot, new keys start above the floor, failed
   writes burn their dot. They call the library directly: a user-approved exception to the integration-only rule,
   for the lattice library only (2026-10-07).
2. **Group commit (Fast).** Concurrent writes share batches; a failing write rolls back only its savepoint; a long
   read does not block writes.
3. **Membership and leases (Fast and Slow).** Static `peers` in Fast; a `nats-server` per test in Slow: joins, leaves,
   expiry with delete markers, refill after an expired lease, owner failover.
4. **Partition suite (Slow; the gate for the format flip).** Three to five members whose peer links pass through a
   fault proxy (drop, delay, duplicate, reorder, partition, heal). Checked histories: convergence after heal, no
   acknowledged write lost, CAS linearizable while the lease is held, no fractured transaction reads.
5. **End to end (Fast).** Management commit fences and dashboard sign-in against TerraBase-backed stores (step 6).

## 14. Benchmark gate

A TS-606 run on 1-OCPU A1 nodes beats today's engine on writes, with equal or lower CPU and memory, before the
cutover ([§17](#17-benchmark-targets)).

## 15. Plan

TS-647, one PR per step. `main` stays deployable on every merge before step 4, because production auto-deploys
every push to `main`.

| Step | Contents |
| --- | --- |
| 1 | Delete the slice-2 surface (`core`/`actors` settings, `StorageCore`, `AnnaDocumentService`, `AnnaShapeService`, `DocumentActors`, the Development guard). Fix dot minting: epoch in `ReplicaId`, floor-seeded keys, burned dots, the same-dot refusal, no allocations in `CausalContext.Max`/`Compact` on the common path. This document rewritten |
| 2 | Group commit: per-database writer thread apart from the read pool, one `SAVEPOINT` per client write, collections in memory. Measured with TS-606 |
| 3 | NATS membership, ring and leases (KV TTL with delete markers); static `peers` for tests; backup shipper by lease (with TS-633), deleting TS-633's interim leases. Then: membership replaces `peer-addresses`; refill after an expired lease; lease checks around owner commits; clock-skew capping |
| 4 | Format flip: `MvRegister` state, HLC stamps, epoch replica ids, joins, per-origin streams with dependency vectors; delete `VersionClock`, `StampingStorageEngine`/`IWriteStamps`, `JournalDigest`, merge v1, baselines and legacy upgrades; a one-time in-place migration. Gate: the partition suite. In the sub-steps below |
| 5 | `_system` as a lattice database on every member; delete the catalog primary; usage `GCounter`s |
| 6 | Owner CAS and conditional transactions on the primary owner, reservations released by the flush callback; end-to-end tests of commit fences and dashboard sign-in |
| 7 | Causal consistency per request; interval and tombstone GC on applied vectors |

Step 4 runs as expand, switch, contract, because every merge rolls out to production member by member (the `deploy`
job on every green main push) and peers refuse any other protocol or format version
(`ReplicationProtocol.Accept`/`Accepts`): until the last sub-step, members of the old and the new build replicate with
each other. Unknown protobuf fields are ignored by older members, so the new state travels beside today's group.

| Sub-step | Contents | Rolls out mixed |
| --- | --- | --- |
| 4a | The register encoding (`LatticeEncoding`, `DocumentWrite`); no caller yet | Yes, nothing changes |
| 4b | Replica identity: a member number and a replica epoch raised on every unclean start, in `.terrabase.replica` | Yes, a new file older builds ignore |
| 4c | Expand: one HLC per member (observing every applied stamp, advancing at most `max-clock-skew`); each document row also keeps its encoded register, written by local writes and joined on apply; groups carry registers in a new field; a member converts its files in place on first open | Yes, merge v1 still decides |
| 4d | Switch: the join decides, LWW reads take the register's winner, tokens hash it; dependency vectors gate apply | Only once every member runs 4c |
| 4e | Contract: delete merge v1, `VersionClock`, `JournalDigest`, baselines, legacy upgrades and `StampingStorageEngine`; one protocol version | Under a deploy freeze, gated by the partition suite |

Merged under TS-634: the design (#50), the lattice library (#51), and slice 2 (#53), whose surface step 1 deletes.

Merged independent follow-up under TS-634 (#84): restart-safe database deletion in the existing TS-643 WAN path. Reconcile records
from bounded pages of the local catalog against a complete home-region heads round; remove missed deletions after restart,
including records without a local file. This needs no TS-647 format flip, per-core actors or memory tier. See
[cross-region deletion](../cross-region.md#placement) and its Fast integration coverage.

Next independent follow-up under TS-634: complete WAN member discovery across direct replies and later heads pages.
Different members can list different views; every discovered member must answer before a round authorizes deletion or
resets staleness. Discovery bounds member count, concurrent requests and elapsed time; retained heads can still be pulled
after its ten-second deadline while cleanup remains postponed. Bounded pagination cursors let consistently slow scans
reach tail databases. A fixed catalog page of at most 100 candidates, bounded presence witnesses and member EOF
certificates let a whole sweep qualify cleanup across rounds; only lagging heads, one reply's worth, are retried.
Once every member holds one, a
deadline-cut fresh scan cannot veto it. Catalog advancement waits for every discovered member's EOF; staleness reset also requires catch-up on every accepted prefix and suffix page.
Historical discovery overflow keeps useful heads and permanently blocks cleanup before dropping unresolved identities.
This needs no TS-647/648/649 changes. See
[cross-region heads](../cross-region.md#how-it-works) and its Fast integration coverage.

## 16. Decisions and open questions

Decided by the user:

1. 2026-10-06: rebuild the core on Anna; keep the public API, collections, SDKs and dashboard; cut over at the TS-602
   wipe without migration code (the migration part is replaced by decision 6).
2. 2026-10-07: replicate inside one region now, across regions later (TS-643).
3. 2026-10-07: lattice-law property tests call the library directly, an exception for the lattice library only.
4. 2026-10-07: membership through NATS (`NATS.Net`); owner operations answer `503` while NATS is unreachable. Static
   `peers` stays for tests and single-node runs (TS-647).
5. 2026-10-07 (TS-647): one engine with SQLite as its storage, no core switch, no Turso; the simpler route now and full
   Anna later; conditional transactions keep today's semantics on the primary owner (this replaces the read-atomic
   decision of the same morning).
6. 2026-10-07 and 2026-10-08 (relayed by COS, TS-677 and TS-634): production keeps its data through a one-time
   in-place migration instead of the TS-602 wipe. This replaces decision 1's "without migration code".
7. 2026-10-08 (COS, pre-1.0 rule): no new v1/v2 names. Version tokens keep their current shape instead of a `tbv2-`
   prefix.
8. 2026-10-08 (TS-647 step 4b, under COS's delegation): a member number is a random non-zero 32-bit value drawn when a
   data directory is first used, instead of the membership KV revision of its first registration. It works the same
   with static `peers` and with NATS. Three members collide with odds of about 1 in 1.4 billion, and a collision stops
   a stream on the same-dot refusal instead of losing a write.

Open:

1. Thread pinning (stage 2) needs `AllowUnsafeBlocks` for `LibraryImport`.

## 17. Benchmark targets

TS-606 has not yet run load against gru-1 (its first run stopped at preflight: `/healthz` through Cloudflare at a
median of 73.1 ms; idle members at 2-6 % CPU and 194-236 MB peak RSS). The latest load numbers for the C# server are
TS-500's local baseline (`ts-benchmark` `bench/csharp-baseline/ts500-20261004*`, one node, three runs), concurrency 16:

| Workload | ops/s | p99 ms |
| --- | ---: | ---: |
| point_read | 10,111-13,710 | 4.4-5.7 |
| point_write | 2,012-2,268 | 54.9-79.2 |
| query_filter_sort | 519-3,188 | 12.6-55.0 |
| transactions | 2,182-2,463 | 54.6-80.7 |
| mixed_90_10 | 8,562-15,686 | 6.0-7.9 |

Writes plateau at about 2,000-2,300 ops/s with p99 above 50 ms: one SQLite transaction per client write, serialized
per database. Stage 1 targets, measured with the same harness and then TS-606 on 1-OCPU A1 nodes:

| Workload (concurrency 16) | Target |
| --- | --- |
| point_write | about 5× today (≥ 10,000 ops/s), p99 ≤ 10 ms |
| transactions (unconditional) | ≥ 2× today |
| point_read, query_filter_sort, mixed_90_10 | no regression |
| CPU and memory | equal or lower at the same load |
