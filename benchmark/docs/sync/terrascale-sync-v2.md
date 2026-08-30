# TerraBase reactive result-view semantics

Status: accepted engine contract, not yet production-qualified.

Authority: ADR 0024 and ADR 0025.

This document defines the transport-independent state machine TerraBase serves
to the Go gateway through private TBEP gRPC. It is not a public protocol.
TerraBase owns no public MessagePack codec, browser socket protocol, or public
binary frame registry.

## Purpose

Reactive sync keeps one bounded authorized TQL result current. It does not
replicate a database or expose native CRDT, causal, storage, or Member state.

Correctness has two paths:

- a live private gRPC stream sends result revisions with low latency; and
- checkpoint resume or snapshot bootstrap restores the result after loss.

Losing a stream cannot lose a durable change. The durable publication log,
view checkpoint, and fresh authorized snapshot define recovery.

## Boundary

TerraBase owns subscription planning, query and authorization dependencies,
result maintenance, view generations, checkpoints, resume, and reset. The Go
gateway opens the private session through `terrabase-go`, applies public
authorization, and maps the typed result into its public HTTP contract.

Public clients never receive:

- native CRDT operations or mutation logs;
- replica identities, frontiers, or dotted version vectors;
- storage, tier, or topology internals;
- rows or fields outside the current authorization result; or
- another subscriber's delivery state.

## Write model

Every durable mutation reaches the Go gateway and then one named TBEP RPC. A
mutation carries a stable client-request identity. Retrying the same identity
and request digest cannot apply it twice. A different digest under the same
identity fails closed.

Client-side optimistic presentation is outside TerraBase. It cannot create a
commit, advance a causal token, or advance a view checkpoint.

## Subscription identity

A setup request names a bounded TQL query, arguments, projection, consistency,
causal-session requirements, result budget, and an optional checkpoint.
TerraBase authorizes and plans the query, records its dependencies, and assigns
a `ViewGeneration`.

The view identity binds tenant, database, principal scope, normalized query,
arguments, projection, authorization generation, schema generation, and result
semantics. Changing any input creates a new generation.

## Result model

Each result revision corresponds to one causally consistent database cut. A
client never receives half of one committed transaction.

Bootstrap returns a bounded snapshot, view generation, result revision,
result digest, and checkpoint. Later revisions use these logical operations:

| Operation | Meaning |
| --- | --- |
| `Insert` | Add one projected result at a stable position or key. |
| `Update` | Replace one result item. |
| `Patch` | Apply bounded typed field changes against an exact base revision. |
| `Remove` | Remove an item that no longer matches or is no longer authorized. |
| `Move` | Move an ordered item without resending its full value. |
| `Reset` | Replace the result with a fresh authorized snapshot and checkpoint. |

The engine emits a patch only when it can prove the base and final digest. It
uses replacement or reset when proof is unavailable.

## Incremental maintenance

The incrementally maintained set includes indexed equality and range filters,
deterministic ordering, bounded top-k, stable projections, and supported policy
predicates. Top-k dependencies include the eligible index boundary, not only
visible rows.

Unsupported, unbounded, or over-budget plans may rerun under a bounded budget
and emit `Reset`. They never emit a guessed patch. Identical authorized plans
may share engine work, but delivery state and accounting remain isolated.

## Authorization

Every setup and resume checks the current policy generation. Permission loss
removes protected data before the new generation advances. Permission gain
uses a bounded diff or a fresh snapshot. A checkpoint records what one
authorized view applied; it never grants access.

## Checkpoints and resume

The checkpoint is opaque, versioned, integrity-protected, bounded, expiring,
and bound to the tenant, database, principal scope, query, projection, view
generation, result revision, retention generation, and key generation.

Resume chooses one of three outcomes:

| Available state | Outcome |
| --- | --- |
| Retained result revisions | Send the missing revisions. |
| Retained native state and bounded reconstruction budget | Reconstruct and send a verified diff. |
| Missing history, changed authorization, or exhausted budget | Send `Reset` with a fresh authorized snapshot. |

A checkpoint below the retained floor produces `CheckpointExpired` and a fresh
bootstrap. The engine never fills a gap by guessing.

## Ordering and acknowledgement

One subscription is revision ordered. Delivery may repeat across reconnect.
Applying the same completed revision twice is safe. A client acknowledges only
after it has applied and verified the complete revision.

Different subscriptions do not imply a global order. Mutation idempotency and
result revision identity are separate.

## Backpressure

Every session has fixed bounds for subscriptions, inbound and outbound bytes,
unacknowledged revisions, queued operations, snapshot size, dependency work,
idle time, and reconnect work.

When a consumer falls behind, TerraBase coalesces equivalent operations. It
then replaces the backlog with `Reset`. If progress still fails, the private
stream closes with a typed resumable overload status. One session cannot retain
an unbounded queue, cache, posting list, or task.

## Security

Private delivery uses TBEP mTLS in production. The Go gateway supplies a
current short-lived principal context. Stream results and errors never contain
credentials, raw grants, other tenant identities, internal routes, storage
paths, replication metadata, or stack traces.

Unknown versions, invalid state transitions, malformed protobuf fields,
oversized messages, invalid nested values, and unsupported required behavior
fail before query work or public-value construction.

## Conformance

The language-neutral semantic corpus covers bootstrap, every result operation,
duplicates, gaps, resume, expiry, authorization changes, slow consumers,
route changes, cancellation, overload, malformed requests, and tenant
isolation. Rust and Go execute the same scenarios through the private gRPC
boundary. Raw protobuf and TLS tests remain separate from semantic vectors.

Performance and bandwidth claims require repository benchmark receipts tied to
the exact engine, Go client, gateway, topology, and configuration.
