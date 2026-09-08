# TerraBase split-target idempotent replay — ready brief

This is a planning/evidence note only. Product source remains read-only for the
preparer; the coordinator owns assignment and acceptance.

## Outcome and current gap

Qualify (and, if the current source still fails, correct) the real
`ScopedShardEngine::create_split_target` caller so the first admitted target
creation persists one immutable split identity and an exact retry is a no-op.
The observed failure is in
`src/engine/shard_split_tests.rs:39-56`,
`encrypted_target_creation_is_durable_and_exactly_idempotent`: the captured
run at `terrabase/.agents/review-current/resume-20260907/core-wave/takeover/shard-target-failure.log`
reported `first target create: Conflict`. That log is historical; current
`open_owned_engine` (`src/engine/tests.rs:96-107`) activates epoch 1, so rerun
against a frozen current source before attributing the failure to production.

The source contract is explicit: `create_split_target` (`src/engine/shard_split.rs:289-328`)
opens/creates the encrypted target, reads the durable target identity, returns
success for the exact identity, rejects a different identity, and only installs
a new identity after inheriting a nonzero, unfenced source origin epoch
(`:307-314`). `persist_target_identity` uses the stable operation
`shard-target-{split_id}` and `Synced` durability (`:213-232`). Do not remove
the epoch/fence guard or turn every `Conflict` into success to make the test
green.

## Ownership and dependencies

The worker may own only `terrabase/src/engine/shard_split_tests.rs` and a
focused evidence note/log. `src/engine/shard_split.rs`, storage schema, route
catalog, encryption configuration, and member/runtime callers are read-only
dependencies. There is no current test-file dirty path in the preparer's
snapshot, but freeze ownership before writing because the shared TerraBase
checkout is moving. `/root/tb_check` (or the coordinator's named equivalent)
owns Cargo/build resources; do not start a competing workspace build.

## Chosen design and replay boundaries

Use the existing split identity derived by `target_identity(context, state)`;
do not mint a fresh operation or target path on retry. The test fixture must
exercise an admitted source writer epoch (`Engine::activate_origin_epoch(1)`
is the established local fixture pattern in `src/engine/tests.rs:106` and
`src/engine/origin_fencing.rs:14-24`) before target creation. Then:

1. Save a valid split state, call `create_split_target` once, and verify the
   encrypted target location and identity are durable.
2. Drop/reopen the adapter and call the same context again. The call must read
   the existing identity and return `Ok(())` without a second metadata write,
   new operation identity, epoch change, route publication, or source effect.
3. Call with a changed context/identity against the same target and require
   `EngineError::Conflict`; retain the existing target identity and route.
4. If a failure remains, isolate whether it is the source epoch/fence,
   target opening/encryption, split-state validation, metadata replay digest,
   or identity mismatch. Report that evidence before proposing a product edit.

The source's general operation ledger already defines replay behavior in
`src/storage/sqlite/mod.rs:2474-2595`: an existing operation with the same
digest returns its recorded receipt; a changed digest returns
`CommitRejection::OperationIdReuse`. A retry after process restart must use the
same split identity and must not infer success from a live target document or
current route.

## Acceptance cases and checks

The focused test must discriminate these cases:

- valid source epoch, first target create succeeds and target identity survives
  adapter drop/reopen;
- exact same context replay returns success and leaves one target identity,
  one durable operation fact/receipt, and unchanged route/epoch;
- changed split ID, bounds, placement epoch, or database incarnation returns
  bounded `Conflict` before mutating the target;
- source epoch zero or fenced returns `Conflict` before target identity write;
- an existing target with a foreign identity is rejected and never rebound;
- a failure/unknown commit does not trigger target deletion or a new operation
  identity; retry resolves the original durable result.

Run the focused selector for
`engine::shard_split_tests::encrypted_target_creation_is_durable_and_exactly_idempotent`
through the shared Cargo owner, capture tested source SHA, command, exit and
log, then run only the relevant shard-split engine selectors if the focused
case passes. A worker report is evidence only; coordinator/Astra must review
the source and acceptance. No broad `make check` claim follows from this
brief.

## Forbidden shortcuts and control

Do not delete the origin-epoch admission guard, weaken identity comparison,
swallow `Conflict`, fabricate a target identity, reuse a source operation key
for target metadata, or accept a historical failure log as current evidence.
Do not edit runtime composition, generated contracts, storage schema, or
serving dispatch. The task is execution-ready under established epoch and
operation-ledger contracts; no outline checkpoint is needed unless the rerun
shows that those contracts conflict. Deviations involving public split
identity, physical-shard admission, encryption ownership, or cross-file
atomicity require Astra/coordinator direction.

Return changed paths (if any), source SHA, exact failure/success evidence,
remaining hypotheses, and whether the focused replay, restart, conflict, and
fenced cases were actually exercised. Do not claim acceptance from a worker
report alone.
