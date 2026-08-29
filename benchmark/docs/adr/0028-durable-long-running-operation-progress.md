---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Durable long-running operations and progress

## Scope

Every TerraBase or TerraScale database action that can outlive an interactive
request deadline, continues in the background, crosses a process restart, or
has multiple durable phases is a long-running operation. It creates a durable
operation resource before work begins.

This includes Region addition and removal, placement and Partition movement,
Member replacement, snapshot bootstrap, repair, restore, backup, PITR,
compaction, tier movement, index build and retirement, schema migration,
relationship cascade, large deletion, export, re-encryption, rebalancing, and
other bounded bulk work. A new feature cannot invent a separate job-status
model.

## Operation resource

An operation has one ULID and one authoritative owner. The durable record
contains:

```text
Operation {
    operation_ulid
    kind
    owner
    tenant_ulid
    project_ulid
    database_ulid?
    target_ulids[]
    requested_by_principal
    client_request_ulid
    state
    progress_generation
    stage_code
    completed_units
    total_units?
    unit
    child_summary
    created_at
    started_at?
    updated_at
    heartbeat_deadline?
    finished_at?
    cancellable
    result_reference?
    typed_error?
    placement_generation?
}
```

The closed states are `Queued`, `Running`, `Waiting`, `CancelRequested`,
`Succeeded`, `Failed`, and `Canceled`. Only the last three are terminal. A
typed waiting reason distinguishes resource admission, dependency, quorum,
remote Region, customer action, and retry backoff. “Running” without a current
stage and durable heartbeat is not valid progress.

`completed_units` is monotone within one progress generation and never exceeds
`total_units`. Percent is derived only when a stable total is known. Unknown or
changing totals produce an indeterminate indicator with exact completed units,
stage, and rate; the service never fabricates a percentage or completion time.
If verified replanning changes the total or unit, it increments
`progress_generation` and publishes a full replacement snapshot.

Stages use stable machine codes and optional localized display text. Units are
typed, such as bytes, records, Partitions, indexes, chunks, or causal intervals.
An ETA is optional, includes its confidence and observation time, and is never
part of correctness. Parent progress aggregates children by real work units,
not an unweighted average of percentages.

## Durability, retry, and fencing

The component that owns the mutation owns the operation journal. TerraBase
persists engine work with its WAL and operation ledger. The Orchestrator owns
cross-Region allocation and placement operations. `TerraScale.Database`
projects authorized status but never invents progress or completion.

State, stage, progress, result, error, and the work's durable checkpoint update
atomically or through an idempotent sequence that recovery can reconcile.
Process loss resumes from the recorded checkpoint or reaches an explicit
terminal failure; it never starts a second logical operation. Retrying the
submission with the same client-request ULID and digest returns the same
operation.

Workers use placement generation, replica-set epoch, and operation generation
as fencing inputs. A stale worker cannot publish progress or a result after
ownership changes. A lease or heartbeat helps detect stalled execution but is
not authority to discard committed work.

Cancellation is a durable request, not an immediate success. The owner stops
at a declared safe point and reports `Canceled`, or completes and reports the
actual result. Operations with irreversible committed phases declare
`cancellable = false` or use a typed compensating operation. Partial durable
outcomes are explicit and retain the identifiers and frontiers required for
safe retry or repair.

## Public API and reactive delivery

After synchronous authentication, authorization, validation, admission, and
idempotency resolution, a public request that starts long work returns HTTP
`202 Accepted`. It includes:

- `Location: /api/v1/operations/{operation-ulid}`;
- `Retry-After` with bounded polling guidance; and
- the initial typed operation resource.

`GET /api/v1/operations/{operation-ulid}` returns the current authorized
snapshot. `DELETE` requests cancellation only when the operation declares it.
Completed creation may return a result reference; clients retrieve the result
with an ordinary typed operation rather than replaying the original mutation.
Operation records and results have an explicit retention and expiry response.

The public reactive WebSocket sends generated MessagePack operation snapshots
and deltas under ADR 0025. Each update carries the operation ULID, progress
generation, monotone update sequence, state, stage, units, timestamps, and
typed result or error. Reconnect uses the public view checkpoint; unavailable
history sends a fresh authorized operation snapshot. Polling is a supported
status fallback, not the engine's execution or correctness mechanism.

Both SDKs expose the same typed `Operation<T>` handle with wait, observe,
refresh, and conditional cancel operations. The Dashboard and Operations
Studio use that SDK contract and display indeterminate progress honestly.

## Adding a Region

Adding a Region is one parent operation with durable child operations for each
database and Partition. Its stable stages are:

1. validate catalog admission, capacity, identity, routing, and policy;
2. allocate and attest two active Members and pooled Region links;
3. select and pin verified source snapshots;
4. transfer, authenticate, and verify snapshot chunks;
5. stream and apply retained causal deltas;
6. reach the required stored, applied, and Merkle-verified frontiers;
7. enter and complete the joint membership generation;
8. prove reads, writes, repair, recovery, and resource isolation; and
9. publish the Region as selectable and release temporary pins.

Snapshot transfer reports verified bytes and chunks. Catch-up reports causal
intervals and lag. Membership reports completed Partitions. The Region is not
`Succeeded`, selectable, or included in a durability receipt until every
required child succeeds and the final catalog decision is durable. Failure
names the incomplete children and preserves safe resumable state.

## Qualification

Tests cover monotone progress, unknown totals, replanning generations,
idempotent submission, duplicate and reordered updates, process and coordinator
failure at every durable boundary, stale-worker fencing, cancellation races,
partial results, authorization changes, reconnect and expired history, parent
aggregation, retention expiry, hostile identifiers and lengths, slow clients,
and noisy-tenant isolation.

Every long-running feature's acceptance test asserts that an authorized user
can observe truthful progress from admission through the exact terminal result.
