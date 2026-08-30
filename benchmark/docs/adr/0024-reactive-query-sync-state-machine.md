---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Reactive query synchronization state machine

## Context

Region replication and public synchronization solve different problems.
Trusted Members need complete native causal state. Public clients need only the
authorized result of bounded queries. Exposing native CRDT frames to a client
would leak internal history, make client identity part of replication, and tie
the public protocol to storage encoding.

The workspace goal separates the planes. ADRs 0022 and 0023 define native
commits and their replication. This record fixes how applied commits become
query result snapshots, revisions, patches, checkpoints, and safe reconnects.

## Product contract

- Public writes are online and server-authoritative.
- Clients may render reversible optimistic presentation while a write is in
  flight. That presentation is not durable CRDT state.
- Clients subscribe to bounded TQL queries, not tables, collections, or raw
  mutation logs.
- Every result is bound to a causal cut, schema generation, query identity,
  authorization generation, and view generation.
- Realtime delivery reduces latency. Snapshot plus checkpoint recovery defines
  correctness.
- The engine sends incremental result operations when it can prove them. It
  sends `Reset` when proof or retained state is unavailable.
- A checkpoint never grants access and never bypasses current authorization.

## Ownership and trust boundary

TerraBase owns query planning, dependencies, view state, result revisions,
checkpoint meaning, authorization enforcement, change summaries, and durable
resume facts.

The Go `TerraScale.Database` gateway owns the public HTTPS connection. It
authenticates the caller, obtains a current signed grant, constructs a
short-lived `PrincipalContext`, and opens a bounded TBEP gRPC sync session
through `terrabase-go`. It cannot invent a result, advance a checkpoint,
weaken a projection, or read raw replication frames.

Dashboard and public SDKs never use TBEP. A public client cannot become a
replication actor or submit a dot, actor counter, replica epoch, native CRDT
delta, causal Merkle proof, or Member acknowledgement.

## Canonical view identity

TerraBase parses TQL into the versioned typed AST, binds arguments and schema,
compiles authorization, and canonicalizes the result contract. The canonical
view-plan digest is:

```text
ViewPlanDigest = BLAKE3-256(
    domain = "terrabase-view-plan/1",
    tenant_ulid,
    database_ulid,
    canonical_tql_ast,
    canonical_arguments,
    projection,
    deterministic_order,
    limit_and_page_shape,
    relationship_expansion,
    consistency_level,
    schema_catalog_digest,
    authorization_scope_digest
)
```

The catalog assigns one `ViewPlanUlid` under ADR 0027 to the coordinated plan
record for that digest. A digest is a content verification value, not an entity
ID. The complete canonical inputs are compared when digests match.

Queries without a deterministic total order add canonical document identity as
the final tie breaker. Offset pagination is not resumable reactive state.
Reactive pagination uses stable cursors and a bounded window.

Two subscribers may share a plan, dependency graph, and materialized result
only when their authorization scope digest and every other identity input are
equal. Per-subscriber delivery state remains separate. Sharing must never make
one subscriber's principal, parameters, or fields visible to another.

Each subscription attempt creates:

```text
ViewInstance {
    view_ulid
    view_plan_ulid
    view_plan_digest
    principal_binding
    authorization_generation
    view_generation
    result_revision
    causal_cut
    retention_generation
}
```

`ViewUlid` is a server-generated ULID. `ViewGeneration` changes whenever the
query contract or visibility domain changes. Revisions are unsigned 64-bit and
monotone inside one generation. They never wrap.

## Durable change publication

Every applied ADR 0022 commit produces a bounded canonical `ChangeSummary` in
the same recoverable publication state machine:

```text
ChangeSummary {
    commit_ulid
    applied_causal_position
    tenant_ulid
    database_ulid
    partition_ulid
    changed_documents_and_incarnations
    changed_field_paths
    changed_relationship_edges
    old_and_new_required_index_keys
    affected_index_generations
    authorization_dependency_tags
    routing_tags
    payload_references
    summary_hash
}
```

Large values stay in content-addressed storage. Matching reads summaries and
indexes before loading payloads. A summary contains enough old and new index
information to decide bounded membership and ordering changes without reading
an unrelated document.

The publication record is durable before `applied` advances. Recovery can
rebuild it from the canonical commit and the named before and after MVCC state.
Realtime notification may be duplicated or lost. The durable publication log
and causal position recover it.

Only this engine publication journal may advance result revisions or issue
reactive checkpoints. A separate in-memory delivery host may cache bounded
derived state, but it cannot mint a checkpoint, claim a committed revision, or
become a second source of publication order.

Segmented posting indexes cover tenant, database, Partition, collection,
document, index generation, key or range, relationship, routing tag, and
authorization dependency. The engine intersects these indexes to find
candidates. It does not create an unbounded posting list for each arbitrary
client predicate.

## Subscription bootstrap without a gap

Bootstrap uses a publication barrier:

1. Authenticate and admit the bounded query and result budget.
2. Compile the plan and dependencies.
3. Allocate `ViewUlid` and `ViewGeneration`.
4. Register dependencies at publication barrier `B` before exposing a result.
5. Select an MVCC causal cut `C` at or before `B` that satisfies the requested
   session token.
6. Execute the authorized query at `C` using required Auto-Indexes.
7. Buffer or journal candidate publications after `C` for this plan under a
   strict byte and work budget.
8. Emit the result snapshot and its checkpoint.
9. Incrementally process publications after `C` in causal and deterministic
   result order.

Registering before the query prevents a commit between query execution and
subscription activation from disappearing. If the buffer exceeds its budget
before snapshot completion, TerraBase selects a newer cut and restarts the
snapshot. It never guesses which changes were missed.

Snapshot frames are:

```text
SnapshotBegin {
    view_ulid
    view_generation
    base_revision
    causal_cut
    schema_generation
    result_shape
    declared_row_and_byte_bounds
}

SnapshotChunk {
    view_ulid
    view_generation
    chunk_ordinal
    rows
    chunk_hash
}

SnapshotEnd {
    view_ulid
    view_generation
    base_revision
    result_digest
    checkpoint
}
```

The client publishes the snapshot to its local cache only after all chunks and
the end digest validate. An interrupted snapshot replaces nothing and can
restart safely.

## Result revisions and operations

One committed transaction produces zero or one atomic result revision per
affected view. It never exposes half of a multi-document commit. Several
commits may be safely coalesced into one revision when the final result and
causal cut are identical to applying each omitted intermediate revision.

An incremental frame is:

```text
ResultDelta {
    view_ulid
    view_generation
    from_revision
    to_revision
    causal_cut
    operations[]
    result_digest
    checkpoint
}
```

Supported operations are:

```text
Insert(identity, position, value)
Update(identity, value)
Patch(identity, canonical_path_operations)
Remove(identity)
Move(identity, old_position, new_position)
Reset(snapshot_descriptor)
```

Document identity includes incarnation. Relationship results include stable
edge identity. A recreated document never aliases a deleted incarnation in a
client cache.

Operations use these rules:

- identity, not array position, selects an existing row;
- ordering uses the canonical query sort plus identity tie breaker;
- removals run before inserts that reuse a visible key;
- a move may accompany an update or patch for the same identity;
- patch paths use the canonical typed path encoding from ADR 0022;
- full replacement wins when its encoded bytes are smaller or patch proof is
  unavailable;
- duplicate deltas whose `to_revision` is already applied are ignored;
- `from_revision` different from the client's applied revision is a gap and
  triggers resume or reset; and
- the result digest covers the complete logical result at `to_revision`, not
  only the operations in the frame.

The engine maintains simple indexed equality, range, ordered, top-k,
projection, and one-hop relationship plans incrementally. For a plan it cannot
prove incrementally, it reruns the bounded query and emits either a verified
diff or `Reset`. A rerun is not permission to perform an unbounded scan.

Top-k plans track the eligible index boundary beyond currently visible rows.
Changing a row outside the result can insert it and evict another row. A
delete, relationship change, or authorization change can remove a row even
when no projected field changed.

## Checkpoint format and meaning

A checkpoint is an opaque, versioned, authenticated capability to request
resume. It is not an authorization grant. The plaintext logical fields are:

```text
ViewCheckpoint {
    protocol_major
    cluster_ulid
    tenant_ulid
    database_ulid
    view_ulid
    view_plan_ulid
    principal_binding_digest
    authorization_generation
    view_generation
    result_revision
    result_digest
    causal_cut_digest
    sparse_partition_positions
    retention_generation
    issued_at
    expires_at
    nonce
    key_id
}
```

The engine encodes the canonical fields with authenticated encryption under a
rotating checkpoint key available to qualified TerraBase Members in every
active Region. The public token exposes only version, key ID, nonce, bounded
ciphertext, and authentication tag. Associated data binds the public API
protocol major and TerraScale environment. A token from another environment,
cluster, tenant, or database fails before lookup.

Small sparse positions may remain inside the token. When the canonical token
would exceed its fixed byte limit, it contains a `CheckpointRecordUlid` and
digest instead. TerraBase stores the bounded record with the same expiry
and tenant accounting. Loss or delayed replication of that record causes a
safe authorized reset, never missing data.

The token means that TerraBase produced this authorized view at the stated
revision and cut. The SDK persists it only after atomically applying and
validating the corresponding snapshot or delta. A malicious client can discard
its own updates by presenting a later issued token early, but it cannot obtain
another principal's data or advance server causal state.

Checkpoint keys rotate with an overlap window longer than the maximum token
lifetime. Compromise revocation may invalidate tokens early and force reset.
Tokens are never logged in plaintext.

## Client acknowledgement

The client sends:

```text
ViewAck {
    view_ulid
    view_generation
    applied_revision
    result_digest
}
```

Acknowledgement lets TerraBase release per-subscriber delivery buffers. It
does not advance replication progress, authorize compaction of database causal
state, or prove authorization. A digest mismatch closes the generation with a
typed `ViewDiverged` result and requires an authorized snapshot.

The server bounds unacknowledged revisions, bytes, and time. It need not retain
every intermediate result operation once a newer snapshot or safely coalesced
revision covers the client.

## Reconnect and cross-Region resume

On reconnect, TerraBase:

1. validates token encoding and authentication before using any embedded ID;
2. authenticates the current principal independently;
3. resolves current tenant, database, schema, and authorization generations;
4. validates the normalized query and projection against `ViewPlanUlid` and
   `ViewPlanDigest`;
5. verifies retention generation and expiry;
6. satisfies the checkpoint's session dependencies locally, fetches them,
   routes to a qualified Region, or returns a typed causal-availability error;
7. chooses delta replay, bounded reconstruction, or reset; and
8. emits a new checkpoint only after producing an authorized result.

Resume has three safe paths:

| Available state | Result |
| --- | --- |
| Retained result revisions for the same view generation | Replay only missing result deltas. |
| Underlying native commits and MVCC cuts remain, and bounded query reconstruction is admitted | Reconstruct the old and current authorized result, emit a verified minimal diff, then continue. |
| View state, required history, authorization generation, or work budget is unavailable | Emit `Reset` with a fresh authorized snapshot and generation. |

Cross-Region resume uses the same rules. TerraBase does not synchronously
replicate every client's delivery buffer across Regions. A target Region can
reconstruct from replicated commits and retained MVCC history when bounded. If
it cannot, reset is the honest fallback. The product must not promise delta-only
resume under arbitrary Region loss.

A checkpoint below the retained floor returns `CheckpointExpired` followed by
an automatic authorized bootstrap. Expiry is expected lifecycle, not database
corruption.

## Authorization transitions

Authorization is a dependency of every plan. A role, policy, membership,
relationship, field projection, or session-revocation change creates a new
authorization generation.

TerraBase never resumes an old generation as if its permissions were current.
It evaluates the new policy first:

- lost visibility emits removals or a fresh snapshot that excludes forbidden
  data;
- gained visibility emits a bounded authorized diff or a fresh snapshot;
- changed field projection removes forbidden fields even when document data
  did not change; and
- a revoked session closes after the removal or fail-closed boundary required
  by policy.

Payload filtering happens before payload retrieval when metadata suffices.
Sensitive historical bytes are never loaded into a public result merely
because the underlying commit is in the candidate interval.

If the connection drops during a revocation transition, reconnect with the old
checkpoint reevaluates current policy and cannot replay the old authorized
payload. The new checkpoint generation advances only after removals or a safe
replacement snapshot have been emitted.

## Slow clients and resource isolation

Every live session and retained resume record has bounds for:

- inbound and outbound bytes;
- frame and decompressed-frame size;
- subscriptions and shared plan references;
- query work and result rows;
- unacknowledged revisions and time;
- dependency edges and candidate publications;
- retained result-log bytes;
- spill bytes and file handles; and
- heartbeat and idle lifetime.

The engine first coalesces operations when composition is provably equivalent.
It then replaces pending deltas with one `Reset` snapshot descriptor. If the
client still cannot make progress, it closes the session with a typed resumable
overload result.

All costs charge the tenant and database even when work runs in a shared plan.
Shared work allocates cost by measured subscriber use. One high-fanout tenant
cannot consume another tenant's query workers, result buffers, spill quota, or
stream admission. Orchestrator scaling reacts to sustained pressure but is not
required to enforce the immediate bound.

## TBEP session states

Private TBEP gRPC exposes the reactive state machine to the Go gateway. The
logical session states are:

```text
Connecting
Authenticated
Subscribing
Snapshotting
Live
Resuming
Resetting
Closing
Closed
```

Every request declares a supported protocol version, session ULID, and
operation identity through the typed protobuf contract. Unsupported required
behavior fails closed. Requests received in the wrong state return a typed
protocol error without allocating query work.

TBEP has typed RPCs for opening, driving, acknowledging, relocating, and
closing an engine sync session. `terrabase-go` applies bounded backpressure in
both directions. Cancellation closes the engine operation and does not orphan
an unbounded view. The Go gateway owns the public representation and cannot
expose native payloads or Member progress clocks.

## Online writes and optimistic presentation

A client mutation includes a client-request ULID, typed operation, arguments,
and optional session causal token. The server returns the
authoritative commit result and resulting session token.

An SDK may attach a reversible optimistic presentation patch to its local
cache. The patch:

- never enters TerraBase persistence or replication;
- never advances a view revision or checkpoint;
- is removed on rejection;
- rebases on authoritative result operations; and
- is identified separately from the server commit.

Disconnect before server acceptance yields an unknown request only when the
server may have received it. The SDK resolves the existing client-request ULID
online. It does not queue a new offline write.

## Loro replacement

The native public sync corpus replaces every `loro-delta/1` and
`loro-snapshot/1` fixture. `terrabase-sync-core` becomes the query-view state
machine defined here. It does not contain `LoroDoc`, Loro peer IDs, Loro
frontiers, Loro values, or native inter-Member CRDT merge.

`terrabase-sync-conformance` owns language-neutral state-machine vectors
for snapshot, delta, duplicate, gap, ack, reconnect, reset, authorization
change, slow consumer, route change, and malformed input. Rust, the Go gateway,
and public SDK tests consume the same vectors. Expected outputs are independent
canonical values, not bytes generated by the implementation under test.

## Required evidence

Before P5 exits, tests and deterministic simulation must cover:

- a commit between dependency registration and snapshot completion;
- multi-document atomic commit publication;
- duplicate, missing, reordered, and coalesced result deltas;
- top-k entry, eviction, move, and deterministic ties;
- document deletion and recreation under a reused visible ID;
- forward and reverse relationship changes;
- connection loss before and after local snapshot publication;
- reconnect in the same and a different Region;
- retained-delta, bounded-reconstruction, and reset resume paths;
- checkpoint expiry, key rotation, tampering, cross-tenant replay, and excessive
  token size;
- authorization grant, loss, field reduction, and disconnect during removal;
- a stale checkpoint after revocation;
- slow, hostile, and non-acknowledging clients;
- one hundred thousand active subscriptions under measured tenant isolation;
- API process loss and replacement with no sticky session dependency;
- malformed protobuf fields, nested values, lengths, versions, state, and
  allocation fuzzing; and
- identical final query results and result digests across Rust, the Go gateway,
  public SDKs, and every selected Region.

Decision-grade sync qualification follows ADR 0026 after the corresponding
query, authorization, reconnect, reset, and convergence scenarios pass on the
exact candidate source. GRU, SFO, and FRA runs cover concurrent writes, WAN
faults, Region failure, cross-Region reconnect, retained catch-up, snapshot
reset, authorization changes, slow consumers, top-k movement, fan-out, and one
hundred thousand subscriptions where the qualified environment supports it.

The live path is event-driven. Receipts measure its poll intervals, hops,
encoding passes, copies, acknowledgements, bytes, CPU, allocations, reset rate,
backlog, convergence, foreground interference, and commit-to-authorized-view
latency. Coalescing and batching must improve the preregistered end-to-end
metric without losing any tail, correctness, authorization, or isolation
guardrail.

The core qualification invariant is:

```text
same authorized database cut + same canonical view contract
    -> same ordered result + same result digest
```

No successful resume may omit a change required to reach that result. No
resume or reset may include data forbidden by current authorization.
