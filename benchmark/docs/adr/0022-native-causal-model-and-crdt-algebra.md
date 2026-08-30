---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Native causal model and CRDT algebra

## Context

TerraBase uses Partition-scoped causal metadata and native delta CRDTs. A
generic statement that merges are commutative does not define tag identity,
removal semantics, counter allocation, canonical bytes, deletion, or safe
compaction.

This record fixes those contracts. The workspace goal remains authoritative.
Transport batch sizes remain measured configuration; correctness and retention
bounds are explicit here or in their owning ADR.

## Scope

This record governs:

- native commit, dot, actor, mutation, and element identity;
- causal contexts and received-interval representation;
- HLC generation and concurrent scalar ordering;
- the initial CRDT datatype state, delta, and merge rules;
- document deletion and incarnation;
- deterministic encoding and hashing;
- the dependency boundary for the native causal and CRDT libraries; and
- the required replacement of every Loro-owned product format.

It does not define Region transport, snapshot transfer, or public query-view
delivery. ADRs 0023 and 0024 own those state machines.

## Identity model

### Stable identifiers

TerraBase-owned entity identities follow ADR 0027. The causal model uses these
ULIDs and numeric coordinates:

```text
TenantUlid      128-bit ULID
DatabaseUlid    128-bit ULID
PartitionUlid   128-bit ULID
ReplicaUlid     128-bit ULID allocated by membership
CommitUlid      128-bit ULID allocated by the origin
ObjectUlid      128-bit ULID allocated at document creation
ObjectIncarnationUlid 128-bit ULID allocated at creation or recreation
ReplicaEpoch    unsigned 64-bit generation allocated by membership
OriginCounter   unsigned 64-bit counter local to one Partition actor
MutationOrdinal unsigned 32-bit index inside one canonical commit
```

The native wire uses exact 16-byte ULIDs. A decoder rejects non-canonical
widths, counter zero when the protocol reserves zero, and numeric overflow.

A replication actor is:

```text
Actor = (ReplicaUlid, ReplicaEpoch)
```

An actor exists independently in every Database and Partition counter domain.
It is not a client, process boot ID, network connection, Region name, or host
address.

A commit is globally identified by `CommitUlid`. It also names its Database,
Partition, and causal dot; those coordinates are validated independently and
are not an alternate commit-ID format.

Inside a known Database and Partition, its causal dot is:

```text
Dot = (Actor, OriginCounter)
```

All mutations in an atomic commit share that dot. Anything needing identity
inside the commit uses:

```text
MutationTag = (CommitUlid, MutationOrdinal)
```

OR-Set additions and other multi-value events use `MutationTag`. Relationship
edges have their own ULIDs under ADR 0021 and ADR 0027. Neither uses HLC, array
position, a payload hash, or arrival order as identity.

The canonical commit contains at most one semantic mutator for the same
object, incarnation, field path, datatype, and datatype subkey. The builder
folds repeated changes into one validated delta before assigning ordinals or
rejects a combination with no defined atomic composition. Initial datatypes do
not derive causality from mutation order inside one commit.

### Counter allocation

One Partition actor has one sequencer. It assigns counters in WAL order. It
may batch or pipeline writes, but counter `N` cannot become durable at the
origin unless every counter below `N` is represented by an earlier durable
commit record in the same actor log.

Counter allocation and the complete canonical commit body enter the same WAL
record. There is no independently durable reservation that can become an
unexplained permanent hole. A failed remote delivery may leave missing counters
at a receiver, whose received-interval set records out-of-order arrivals
explicitly.

After restart, an actor cannot issue another counter until it has reconciled
its local actor log and high-water mark with both current local seats. A commit
stored by the peer before the origin crashed is recovered under its original
counter. If the peer needed for RF=2 reconciliation is unavailable, the Region
cannot originate a new write for that Partition. This rule permits parallel
local storage without allowing counter reuse.

A counter never wraps. Membership retires the actor before exhaustion. Loss of
the durable sequencer state forces a new `ReplicaEpoch`; disaster recovery may
not guess the old counter or reuse the old actor.

### Idempotency

`ClientRequestUlid` is the public retry identity. It is not a dot and never
enters a causal vector. TerraBase persists this tuple in the same atomic commit
batch:

```text
IdempotencyRecord {
    tenant_ulid
    database_ulid
    client_request_ulid
    canonical_request_digest
    commit_ulid
    outcome_state
    response_digest
    retention_deadline
}
```

Reuse with the same request returns or completes the recorded outcome. Reuse
with a different request digest returns `IdempotencyConflict`. A timeout after
one local Member has stored the commit is an unknown or partial outcome, not a
rollback. The retry must discover or finish the same commit before a new
commit may be minted.

The complete outcome remains retryable for seven days after its terminal
result. Until day thirty, a compact floor retains the client-request ULID,
request digest, commit ULID when one exists, terminal outcome code, and expiry.
A same-digest retry during that interval returns
`IdempotencyResultExpired`, includes the retained commit ULID and outcome code,
and never executes the mutation again. A different digest still returns
`IdempotencyConflict`. A client-request ULID more than thirty days old is
rejected as `IdempotencyExpired` at admission, so the same old retry cannot
execute after the floor is collected. A client-request ULID more than five
minutes ahead of admission time fails as `InvalidClientRequestUlidTime`. The
ULID timestamp is used only for these admission bounds; server timestamps
govern record retention, and the ULID timestamp never proves freshness or
causality.

## Causal state

### Partition progress

For each actor, a Partition clock stores:

```text
ActorProgress {
    through               // every counter 1..through is present
    received_intervals[]  // normalized, non-overlapping intervals above through
}
```

`received_intervals` contains counters actually present above `through`.
Insertion merges adjacent intervals and advances `through` when the next
interval closes the prefix. Implementations may use another bounded
representation only if it produces the same membership answers and passes the
same model tests.

The complete Partition clock is a sorted map from `Actor` to
`ActorProgress`. It answers whether a commit is stored, which intervals a peer
lacks, and whether a snapshot dominates a local frontier. Receiving counter
100 never proves counters 1 through 99.

Gap state has a byte and interval budget. Exceeding it triggers range repair
or snapshot repair. It never collapses an unknown hole into a received prefix.

### Dotted causal context

Every commit has one event dot and a causal context containing the dots whose
effects the writer observed and intends the mutation to follow. The logical
version is:

```text
DottedVersion {
    context
    dot
}
```

Event `A` happens before event `B` exactly when `A.dot` is contained in
`B.context`, directly or through a summarized prefix. Two events are
concurrent when neither contains the other. Arrival time, WAL position at
another Member, QUIC stream order, and HLC do not change that relation.

A transaction reads from one named MVCC cut and emits one context that covers
all values it validated. A blind typed mutation uses the smallest object or
field context required by that datatype. It does not copy the full Partition
clock onto every field.

CRDT state keeps a causal kernel consisting of retained live events and a
summarized context of events already observed. Payloads for dominated events
may be removed, but their causal meaning remains until the stable frontier
permits summarization.

### Context normalization

Contexts have one canonical form:

- actors sort by canonical actor bytes;
- prefixes and intervals are minimal and non-overlapping;
- an interval adjacent to the prefix advances the prefix;
- an empty actor entry is omitted;
- retired actors remain addressable through a versioned summarized floor; and
- decoding a non-canonical form normalizes before comparison but canonical
  hashing always uses the normalized form.

No context operation may infer a counter that was not received or covered by
a verified snapshot.

## Hybrid logical clock

An HLC stamp is:

```text
HlcTimestamp {
    physical_unix_millis: u64
    logical: u32
}
```

Each actor persists its last issued stamp with its sequencer state. Local send
and receive follow the standard HLC maximum rule. Logical overflow blocks and
waits for physical time to advance rather than wrap.

HLC is metadata for human-time navigation and a deterministic tie breaker for
concurrent values. Causal dominance is evaluated first. A causally later value
wins even when its physical component is lower.

For concurrent register candidates, the total comparison key is:

```text
(
    HlcTimestamp,
    ReplicaUlid canonical bytes,
    ReplicaEpoch,
    OriginCounter,
    MutationOrdinal
)
```

Members admit a physical component at most 500 milliseconds ahead of the local
clock. A structurally valid, authenticated candidate between 500 milliseconds
and five minutes ahead may enter a separate per-lane byte-bounded quarantine
journal for at most five minutes. Quarantine does not advance canonical
`stored`, `applied`, or causal progress. Admission later revalidates the
candidate and writes the canonical WAL commit before progress advances;
expiration records `FutureClockRejected` and removes it. A value more than five
minutes ahead, or malformed, unauthenticated, hash-mismatched, or over-limit
input, is rejected before the canonical WAL and is not quarantine data. The
500-millisecond admission bound may be lowered but never disabled or raised
without a new compatibility and qualification decision.

## Native datatype contract

### Common rules

Every datatype defines:

- canonical state;
- a validated typed mutator;
- the delta emitted by that mutator;
- an associative, commutative, and idempotent join;
- visible materialization;
- causal and payload garbage-collection rules;
- snapshot encoding; and
- limits for value size, nesting, event count, and decode allocation.

A mutation names the expected datatype kind. A kind mismatch is a typed error,
not a coercion. The decoder validates the complete delta before allocating
unbounded state or changing a document.

A delta batch may group independent deltas. Calling a batch a causal delta
interval requires proof that it contains a contiguous actor interval and the
context required by the delta-interval construction. Otherwise it is a set of
joinable deltas. Transport ordering is never a merge precondition.

### LWW register

`LwwRegister<T>` stores:

```text
LwwState<T> {
    context
    winner: optional Candidate<T>
}

Candidate<T> {
    value_or_remove
    dot
    mutation_ordinal
    causal_context
    hlc
}
```

Join unions the contexts, removes candidates dominated by the other state,
and selects from the causally maximal candidates. The field's concurrent
assignment/removal policy is applied first. HLC selects among candidates that
remain at the same policy precedence. The joined context records every
considered dot, including discarded candidates, so a discarded value cannot
reappear after a later merge.

For states with live candidate sets `L1` and `L2` and contexts `C1` and `C2`,
the pre-selection live set is exactly:

```text
(L1 intersect L2)
union { x in L1 where x.dot is not in C2 }
union { x in L2 where x.dot is not in C1 }
```

The joined context is the normalized union of `C1` and `C2`. For `remove-wins`,
any concurrent removal excludes concurrent assignments and the HLC key selects
among removals. For `add-wins`, any concurrent assignment excludes concurrent
removals and the HLC key selects among assignments. Plain LWW applies the HLC
key to all concurrent candidates. MV retains the complete set. State uses
candidate identity, not value equality, for the intersection.

A field removal is a candidate with no value. The schema chooses LWW,
add-wins, or remove-wins policy. Schema-free field removal uses remove-wins for
concurrent assignment. A causally later assignment can recreate the field.

### MV register

`MvRegister<T>` uses the same candidate and context model but retains every
causally maximal concurrent candidate. Join removes only dominated candidates.
Canonical materialization sorts candidates by the HLC comparison key and
returns their causal identities with their values. Resolving an MV register is
a new mutation whose context covers the versions being resolved.

### Grow-only and PN counters

`GCounter` is a sorted map from `Actor` to a checked unsigned 128-bit
component. An increment by `d` emits the actor's new absolute component, not a
bare increment that depends on exactly-once delivery. Join takes the maximum
component for each actor. Materialization is the checked sum of components.

`PnCounter` contains two `GCounter` maps, positive and negative. Increment
changes the positive component; decrement changes the negative component.
Materialization is the checked signed difference. A schema may impose a
narrower range. Overflow rejects the local mutation and quarantines malformed
remote state. It never wraps or saturates.

Counter components are per object field and actor. Stable actor summarization
may fold retired components into a versioned base only when ADR 0023's stable
frontier proves no retired-actor delta can return.

### Add-wins observed-remove set

`AddWinsSet<T>` is an observed-remove dot kernel:

```text
AddWinsSet<T> {
    entries: value -> set<MutationTag>
    context
}
```

Add inserts a new `MutationTag`. Remove carries the observed tags for that
value and adds them to the context. Join retains tags present in both states,
plus tags present on one side that the other side's context has not observed.
A concurrent add is therefore not in the remover's context and survives.

### Remove-wins set

`RemoveWinsSet<T>` stores causally maximal add and remove events per value. A
remove is its own tagged event. Membership requires an add that is causally
after every retained remove. A concurrent remove defeats an add. An add that
observes all removes can restore membership. Join unions contexts and retains
only causally maximal events. Stable-frontier collection may summarize losing
events but cannot discard the causal floor needed to suppress their replay.

For each value, add and remove event sets use the same context-survivor formula
as registers. After join, the value is visible exactly when at least one live
add remains and every live remove dot is contained in that add's causal
context. A remove after or concurrent with an add hides it. An add whose
context contains all live removes restores it.

The schema must choose add-wins or remove-wins. A generic `ORSet` name in an
API or persisted value is rejected unless its policy is encoded.

### OR map and JSON objects

`OrMap` applies the selected key-presence policy to each key and stores a
nested typed CRDT value under the live key incarnation. TerraBase's
schema-free JSON object patch uses remove-wins key presence. A key assignment
that causally follows the removal creates a new key incarnation. Concurrent
nested mutations merge only when they address the same live incarnation.

Map recursion has explicit depth, key-count, payload-byte, and mutation-count
limits. Canonical paths are arrays of typed segments, not ambiguous dotted
strings. Declared schemas may encode segments as numeric field IDs. Schema-free
segments retain exact UTF-8 bytes after JSON decoding and do not apply Unicode
normalization.

Whole-document replacement compiles to one atomic commit containing
assignments for supplied paths and removals for paths visible in the replaced
incarnation but absent from the new value. It is not one opaque LWW blob unless
the caller explicitly chooses that datatype.

### Flags

A typed `Flag` explicitly selects `enable_wins` or `disable_wins` causal event
semantics. Concurrent enable and disable resolve according to that policy; a
causally later event always wins. There is no ambiguous schema-free flag.
Ordinary JSON booleans remain LWW registers.

### Document tombstones and incarnations

Every document mutation names `ObjectUlid` and `ObjectIncarnationUlid`. Deletion
creates a remove-wins document tombstone event. Updates concurrent with the
delete remain in retained history but are not visible. An update causally
after the delete is rejected unless it explicitly creates a new incarnation.

A recreate operation must observe the tombstone and mint a new
`ObjectIncarnationUlid`. Concurrent recreates are concurrent incarnation
candidates. Their mutation tags carry causality but are not incarnation IDs.
The default document policy selects one through the LWW comparison key while
preserving the losing causal facts. A schema may opt into an MV incarnation
result. A late mutation for an older incarnation never applies to the current
one.

Delete, recreate, restore, PITR, relationship edge handling, index entries,
and reactive publication all carry the incarnation identity. Reusing a plain
document ULID cannot erase a tombstone.

### Native relationships

Relationship edges use the same native rules. The edge's stable application
ULID is its identity. Edge properties are an `OrMap` of typed values. To-one
endpoints are LWW or MV registers. To-many membership uses an explicitly
selected set policy. ADR 0021 continues to govern cardinality, integrity modes,
reverse indexes, traversal, and delete behavior.

## Canonical encoding and hashing

The first native storage and replication codec is `terrabase-native/1`.
Snapshots use `terrabase-snapshot/1`. These names replace
`loro-delta/1` and `loro-snapshot/1`; the old codec IDs are unsupported after
the greenfield cutover.

Canonical encoding follows these rules:

- unsigned integers use minimal unsigned LEB128 unless a fixed width is named;
- signed integers use minimal ZigZag LEB128;
- fixed-width integers are little-endian;
- identity bytes have exact lengths and no textual form on the native wire;
- map keys sort by their canonical encoded bytes and duplicates are rejected;
- sets and intervals sort and deduplicate before encoding;
- strings are valid UTF-8 with exact decoded bytes and no implicit Unicode
  normalization;
- integers and floating-point values retain distinct type tags;
- floating-point values are finite IEEE 754 binary64, negative zero normalizes
  to positive zero, and NaN or infinity is rejected;
- lengths, nesting, element counts, and decompressed bytes are checked before
  allocation;
- unknown major versions and unknown required features fail closed; and
- a decoder may accept a documented non-canonical minor representation, but
  it must normalize before persistence, comparison, or hashing.

BLAKE3-256 over domain-separated canonical bytes is the initial content hash
for commits, request digests, causal summaries, Merkle nodes, snapshot chunks,
and manifests. Each domain has a fixed ASCII prefix and codec major version.
A checksum proves identity and corruption detection, not authorization.

Generated protobuf messages are the bounded private TBEP control envelope under
ADR 0025. Native CRDT and snapshot bytes are opaque length-bounded fields inside
that envelope. A payload is validated against its declared native codec before
any progress acknowledgment. The Go gateway never receives raw CRDT, causal,
snapshot, or replication payloads.

Compression occurs after canonical encoding. The receiver authenticates the
frame, enforces compressed and declared uncompressed bounds, decompresses into
a bounded sink, validates the canonical payload hash, and only then decodes.
The initial required algorithms are `none` and Zstandard. Levels and size
thresholds are measured calibration, not persisted semantics.

## Library and dependency boundaries

Implementation uses two deterministic Rust libraries:

```text
terrabase-causal
    identities, dots, contexts, clocks, intervals, HLC, canonical codec

terrabase-crdt
    datatype state, typed mutators, deltas, joins, materialization, collection
```

`terrabase-crdt` may depend on `terrabase-causal`. Neither library may depend
on storage, WAL, query, indexes, networking, QUIC, TBEP, NATS, wall-clock I/O,
or an async runtime. Tests inject time and identity allocation.

The storage engine persists their canonical types. Replication transports
validated native commits. Reactive sync consumes committed materialized change
summaries and query dependencies. It does not expose raw CRDT state to public
clients.

`terrabase-replication-core` and `terrabase-sync-core` use these native
contracts. `terrabase-sync-conformance`, the TBEP protobuf schema, generated
Rust and Go bindings, vectors, benchmarks, gateway tests, and SDK conformance
replace every Loro codec or type. No adapter translates live Loro state into
native state because no customer data exists.

## Validation and failure behavior

Local mutations validate identity, authorization generation, schema, causal
context, datatype kind, payload limits, arithmetic range, and canonical form
before WAL admission.

Remote mutations additionally validate origin membership at the commit's
replica-set epoch, dot and counter domain, request and payload digests, HLC
skew, mutation ordinals, and codec capabilities. Invalid remote input does not
advance `stored`, `applied`, or causal progress. Only an authenticated,
structurally valid future-skew candidate may enter the separate quarantine
journal. Every other invalid class is rejected, closes or isolates the
offending lane according to policy, and records a bounded secret-safe receipt.

Unknown datatype kinds, required features, codec majors, or schema majors fail
closed. They never materialize as an opaque value that other Members may hash
differently.

## Required evidence

Before P1 exits, each datatype and the shared causal kernel must pass:

- join associativity, commutativity, and idempotence properties;
- permutation, duplication, partition, and retry tests;
- causally ordered and concurrent mutation tests;
- native golden vectors with independent expected canonical bytes and hashes;
- snapshot, compaction, restore, and retired-actor replay tests;
- malformed, oversized, non-canonical, overflow, and allocation fuzzing;
- model tests for received-interval normalization and causal containment;
- cross-platform deterministic materialization and hashing tests; and
- differential benchmark workloads against Loro, Yrs, and Automerge where the
  same semantics can be represented.

The benchmark comparison is evidence about cost. It is not an oracle for
correctness and does not keep any compared library in the product.

## Rejected shortcuts

- A new ULID for every field mutation wastes identity bytes and does not
  replace causal context or `MutationTag`.
- A timestamp alone cannot prove causality or safe removal.
- Arrival-order LWW diverges after reordering.
- A bare `increment(1)` replicated without a dot or absolute component depends
  on exactly-once delivery.
- Permanent tombstones without a stable-frontier rule leak storage forever.
- Visible JSON equality cannot replace causal-state equality.
- Wrapping Loro types behind TerraBase names is not a native engine.
- Sharing the inter-Member CRDT stream with public clients leaks trust and
  prevents query-scoped synchronization.
