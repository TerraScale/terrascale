# TerraScale reactive sync v2

Status: accepted replacement contract, not yet qualified.

Authority: workspace `GOAL.md`, especially PD-2026-08-29-05 and
PD-2026-08-29-08, plus ADR 0019.

Protocol identity: `terrascale.sync.v2`.

The previous Loro payload, collection subscription, and server-ack-only v2
protocol is superseded. Its vectors are historical implementation evidence and
must not be extended as the current contract.

## Purpose

Reactive sync keeps a bounded, authorized query result current. It does not
replicate a database or expose the Region replication log.

The contract has two independent paths:

- realtime push sends current result changes with low latency; and
- checkpoint resume or snapshot bootstrap restores correctness after loss.

A lost WebSocket cannot lose a durable change.

## Boundary

Public TypeScript and .NET SDKs speak this protocol to
`TerraScale.Database`. The Database API translates typed requests to TBEP.
TerraBase owns subscription planning, data and authorization dependencies,
result maintenance, view generations, checkpoints, and recovery.

Dashboard uses the same public TypeScript client. It receives no private route,
header, database shortcut, or raw TBEP access.

Clients never receive:

- native CRDT operations;
- Loro updates;
- replica identities or dotted version vectors;
- unfiltered mutation logs;
- rows outside the current authorization result;
- storage or topology internals; or
- another client's subscription state.

## Transport

The initial binding is one authenticated secure WebSocket per API origin.
Binary MessagePack frames multiplex control, mutation outcomes, subscriptions,
result data, checkpoints, heartbeats, and typed errors.

Ordinary REST APIs may bootstrap a session when the public contract requires
it. Polling is not the recovery path. WebTransport may bind the same state
machine later only after browser support, security review, and benchmark
receipts justify it.

## Client write model

Every durable mutation reaches the server. There is no offline mutation queue,
offline CRDT actor, local replication history, or later merge of disconnected
writes.

A connected client may apply a reversible optimistic presentation associated
with its idempotency identity. That presentation:

- is not durable;
- has no `CommitId` or replica actor;
- cannot advance a causal token or view checkpoint;
- rebases on the authoritative accepted result; and
- rolls back or enters typed conflict UI on rejection.

A write attempted while disconnected fails clearly. Cached reads may remain
available under an explicit stale-data contract.

## Subscription identity

A subscription request contains:

- client-local subscription identity;
- normalized typed TQL query and parameters;
- bounded projection;
- consistency and causal-session requirements;
- requested patch capabilities;
- last accepted view checkpoint when resuming; and
- client limits and protocol capabilities.

TerraBase authorizes and plans the query, records its data, index-range, and
policy dependencies, and assigns a `ViewGeneration`.

The view identity includes principal, tenant, Project, database, normalized
query, parameters, projection, authorization generation, and result semantics.
Changing any of them creates a new generation.

## Result model

Each emitted result revision corresponds to one causally consistent database
cut. The client never applies half of one revision.

Initial bootstrap returns:

- the current authorized result snapshot;
- its causal session token where requested;
- view generation and result revision; and
- a checkpoint valid after the snapshot is applied.

Subsequent result operations are:

| Operation | Meaning |
| --- | --- |
| `Insert` | Add one projected result at a stable position or key. |
| `Update` | Replace one small result item when replacement is cheaper. |
| `Patch` | Apply bounded typed field changes. |
| `Remove` | Remove an item that no longer matches or is no longer authorized. |
| `Move` | Move an ordered item without resending its full payload. |
| `Reset` | Replace the result with a new authorized snapshot and checkpoint. |

The server selects patch or replacement by encoded size and client capability.
It never emits a patch whose base revision is ambiguous.

## Incremental maintenance

The first incrementally maintained query set includes:

- indexed equality filters;
- indexed ranges;
- deterministic ordering;
- bounded `take` and top-k;
- stable projections; and
- policy predicates supported by engine-maintained relation indexes.

Top-k dependencies include the eligible index boundary, not only visible rows.
A mutation outside the current result can enter it and evict another row.

Unsupported, unbounded, or over-budget plans rerun and emit `Reset`.
Aggregations, joins, full-text, spatial, vector, and custom-analyzer queries do
not receive guessed incremental patches.

Identical authorized query plans may share execution. Per-client delivery state
remains bounded and isolated.

## Authorization

Authorization is part of result correctness:

- every setup and reconnect checks the current policy generation;
- payload filtering happens before protected payload retrieval;
- permission loss emits removals before the new generation advances;
- permission expansion creates a new view generation;
- bounded expansion may emit an authorized diff;
- unbounded or uncertain expansion sends a fresh snapshot; and
- a Region that lacks the required policy generation cannot authorize the
  subscription.

A checkpoint records what one authorized view applied. It never grants future
access and cannot bypass a current authorization check.

## View checkpoints

The public checkpoint is opaque, versioned, integrity-protected, and bound to:

- tenant, Project, and database;
- principal and authorization generation;
- normalized query, parameters, and projection;
- view generation and result revision;
- sparse per-Partition delivery positions needed by the view;
- retained-history generation and expiry; and
- the issuing protocol and key generation.

Clients compare checkpoints only for equality and replacement. They do not
decode, edit, merge, or construct causal vectors.

On reconnect, the server verifies the token and current authorization, then
either:

- resumes with missing authorized result operations;
- produces a bounded diff into a new view generation;
- sends an authorized snapshot and new checkpoint;
- returns `CheckpointExpired` and bootstraps automatically; or
- returns a typed denial without protected payload.

## Logical frame families

The generated registry includes these logical families:

| Family | Examples |
| --- | --- |
| Session | hello, accepted, resume, drain, close |
| Authentication | refresh required, authorization generation changed |
| Subscription | subscribe, subscribed, change, unsubscribe |
| Result | snapshot start, snapshot chunk, snapshot end, patch batch, reset |
| Mutation | request correlation, accepted outcome, rejected outcome, reconciled presentation |
| Checkpoint | applied revision, checkpoint issued, checkpoint refused |
| Causality | session token, dependency wait, route change |
| Flow control | credit, slowdown, coalesced, overload |
| Liveness | heartbeat, deadline, cancellation |
| Error | closed typed error envelope |

Exact frame numbers, field numbers, bounds, and MessagePack forms come from one
generated schema. The protocol has no generic event or arbitrary JSON frame.

## Ordering and delivery

One subscription result stream is revision ordered. Delivery is at least once
across reconnect, and applying the same revision or operation twice is safe.
The client acknowledges only after it applies the full revision.

Different subscriptions do not imply a global order. One session causal token
can request read-your-writes or monotonic reads across the sparse Partitions it
has touched.

Mutation idempotency and result delivery identity are separate. A retried
mutation cannot execute twice, and a repeated result patch cannot apply twice.

## Backpressure

Every session has limits for:

- connection and subscription count;
- inbound and outbound bytes;
- unacknowledged revisions;
- queued result operations;
- result snapshot size;
- query and dependency cost;
- heartbeat and idle duration;
- mutation concurrency; and
- reconnect work.

When a client falls behind, the server coalesces safe operations. It then
replaces the backlog with a fresh snapshot and checkpoint. If progress still
fails, the server closes with a typed resumable overload result.

One client cannot retain an unbounded mutation list, result queue, posting
list, payload cache, or server task.

## Security

The transport uses the public API's authenticated origin, audience, CSRF and
CORS policy where applicable, request limits, and audit rules. Long-lived
sessions refresh authorization before their short-lived grant expires.

Frames and errors never contain credentials, raw authorization grants, other
tenant identifiers, internal routes, storage paths, native replication
metadata, stack traces, or raw remote exceptions.

Compression, decompression, projection, and patch application are bounded
before allocation. Unknown versions, kinds, codecs, fields that violate the
schema, digests, or generation transitions fail closed.

## Compatibility and migration

The replacement keeps the `terrascale.sync.v2` product identity but uses a
new generated schema and corpus. No old Loro frame decodes as a new frame.
During cutover, the public endpoint selects one complete protocol generation at
handshake. A session cannot mix generations.

The old Rust, TypeScript, and .NET vectors remain historical evidence. New
conformance fixtures must use a new corpus generation and cannot preserve Loro
bytes for compatibility.

## Conformance

Rust, TypeScript, and .NET run one semantic state-machine corpus covering:

- snapshot bootstrap;
- insert, update, patch, remove, move, and reset;
- duplicate and reordered delivery;
- reconnect and resume;
- expired history;
- optimistic acceptance and rejection;
- idempotent mutation retry;
- read-your-writes across Region change;
- permission loss and expansion;
- stale authorization generation;
- slow consumer coalescing and reset;
- session grant refresh and expiry;
- malformed and oversized frames; and
- tenant isolation.

Byte-level vectors cover the generated MessagePack schema in each language.
Fault tests drop connections at every state transition. Performance and
bandwidth claims require TerraBase.Benchmark receipts.

## Non-claims

This document does not claim that the replacement protocol is implemented,
released, faster, or compatible with existing v2 clients. Until the new corpus
passes in all three implementations, the sync story remains decided but
unqualified.
