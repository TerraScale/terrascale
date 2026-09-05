---
date: 2026-08-29
authority: ../../../GOAL.md
---

# Durable long-running operations and progress

## Decision

Any database or infrastructure action that outlives an interactive request,
continues after a restart, or has multiple durable phases creates one durable
operation resource before work begins. The resource is the shared model for
Region changes, Member replacement, snapshot bootstrap, repair, restore,
backup, PITR, index work, schema migration, export, re-encryption,
rebalancing, and large deletion.

An operation has one ULID and one authoritative owner. Its durable record
contains the operation kind and targets, tenant and database identities,
request identity, state, progress generation, stage code, completed and
optional total units, unit type, child summary, timestamps, cancellation
capability, result reference, typed error, and placement generation when
applicable. Region targets use registered uppercase `RegionCode` values;
provider names do not enter the record.

The states are `Queued`, `Running`, `Waiting`, `CancelRequested`, `Succeeded`,
`Failed`, and `Canceled`. Only the last three are terminal. Progress is
monotone within a generation and never exceeds a known total. Unknown totals
show exact units and stage without an invented percentage or completion time.
Changing a verified total increments the generation and publishes a complete
replacement snapshot. Parent operations aggregate children by their work
units.

## Durability and fencing

The component that owns a mutation owns its operation record. TerraBase
persists engine work with the same durable SQLite transaction and journal
boundaries used by the engine; the Orchestrator owns cross-Region allocation
and placement operations. State, progress, result, error, and the resumable
checkpoint update atomically or through an idempotent sequence that recovery
can reconcile.

Retrying a submission with the same client-request ULID and request digest
returns the same operation. Placement generation, replica-set epoch, and
operation generation fence workers so stale workers cannot publish progress
or results after ownership changes. Cancellation is a durable request: the
owner stops at a declared safe point and reports `Canceled`, or completes and
reports the actual result. Irreversible phases are non-cancellable or use a
typed compensating operation.

## API and delivery

After authentication, authorization, validation, admission, and idempotency
resolution, a request that starts long work returns HTTP `202 Accepted` with
`Location: /api/v1/operations/{operation-ulid}`, bounded `Retry-After`, and
the initial operation resource. An authorized GET returns the current
snapshot; DELETE requests cancellation only for cancellable operations.
Completed work returns a typed result reference, and operation records honor
an explicit retention policy.

Private TBEP sends typed operation snapshots and deltas to the Orchestrator
and CLI. Each update carries the operation ULID, progress generation,
monotone update sequence, state, stage, units, timestamps, and typed result or
error. If history is unavailable, the owner sends a fresh snapshot. Public
SDKs expose the same typed operation behavior for wait, observe, refresh, and
conditional cancellation.

Adding a Region is one parent operation with child operations for each
database. It validates policy and capacity, allocates two active Members,
transfers and verifies a native TerraBase backup snapshot, tails causal deltas,
reaches stored/applied/reconciled frontiers, joins membership, proves reads, writes,
repair, recovery, and isolation, then publishes the Region as selectable.
The Region is not selectable until all required children and the final catalog
decision are durable.
