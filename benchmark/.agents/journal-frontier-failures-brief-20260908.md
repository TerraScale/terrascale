# TerraBase journal/frontier failure briefs

Read-only preparation note. No product source, test source, human-owned
instructions, manifests, or build artifacts were changed by this lane.

## Ready queue and ownership

| Outcome | Current evidence | Write owner | State | Dependency/resource |
|---|---|---|---|---|
| Restart-safe shard journal catch-up preserves the source publication cursor and refuses an unprovable history gap | `src/engine/shard_supervisor.rs:185-220` starts catch-up at `0`, then persists `catchup.applied_through()` only after a successful page; `src/engine/shard_catchup.rs:87-125` reads the publication boundary and rejects an empty page below it. The prior cohort records the restart boundary as a known failure in `.agents/review-current/resume-20260907/core-wave/shard-correction.md:39`. | One worker owns `src/engine/shard_supervisor.rs`, `src/engine/shard_catchup.rs`, and the focused shard supervisor tests that prove the restart seam. Do not edit split state/route contracts owned by another lane. | **Ready, unassigned** | Uses existing `tb_checks`/`/root/tb_check` Cargo owner; submit selectors and source cohort instead of starting a competing build. No route-serving activation. |
| Retention frontier advances only from authenticated, contiguous source-journal acknowledgements across every required peer | `crates/terrabase-server/src/replication_frontier.rs:18-101` already implements required-peer minimum and monotonic durable submission. Physical acknowledgement calls retention with `writer.durable_sequence()` at `crates/terrabase-server/src/replication_peers.rs:1080-1091`; causal receipt calls it with `CausalDriver::acknowledged_source_cursor()` at `:1038-1060`; the causal cursor folds only `checkpoint` entries in `crates/terrabase-tbep/src/production_causal.rs:179,406-445`. Registration currently occurs from the `required` argument in `replication_peers.rs:273-277`; `build_peer_databases` validates physical/causal membership at `production_data_runtime.rs:372-430`. | One worker owns only frontier registration/session-loss/replacement plumbing and its focused tests in `crates/terrabase-server/src/replication_frontier.rs`, `replication_peers.rs`, and the narrow runtime construction seam. Do not redesign `ReplicationProgress` or `durable_peer_frontier`. | **Ready, unassigned, conditional on duplicate check** | First compare current worker/source hashes with `.agents/replication-retention-caller-next.md`; if that owner is active, hand off evidence rather than duplicate. Shared Cargo owner `/root/tb_check`. |

Active turns, open handles, implementation/review/verification status were not
available from this planning lane; the coordinator must refresh the live census
before assigning. These are unassigned ready outcomes, not acceptance claims.

## Brief A: restart-safe split journal catch-up

### Outcome and caller

The production caller is `ShardSplitSupervisor::step` in
`src/engine/shard_supervisor.rs`. After range backfill completes, a restart
must resume after the last source publication durably accepted by the target;
after each successful page the durable split state must contain that exact
source cursor. A crash before the first page must resume at the explicit
backfill boundary (`0` in the current supervisor because snapshot copy does
not preserve original publication identities), then replay every retained
publication through the real `ShardJournalCatchUp` adapter. A missing/expired
publication page below the source publication boundary must fail closed as
`SourceHistoryGap`, never be treated as caught up.

### Ownership and source contract

Write paths are limited to `src/engine/shard_supervisor.rs`,
`src/engine/shard_catchup.rs`, and focused tests adjacent to those modules.
Read-only contracts are `src/shard_split/lifecycle.rs:185-233`,
`src/shard_split/state.rs:143-156`, and
`src/engine/shard_split.rs:1410-1480`. The target application contract is
`Engine::apply_replicated_publication`, which binds the complete source
operation identity and makes retry after a crash safe. Do not change route,
state encoding, or public API files in this task.

### Design and failure behavior

Keep the cursor authoritative in the durable `DurableSplitState` field
`target_applied_through`; the in-memory adapter cursor is only the current
worker view. On each bounded page, read strictly after the persisted cursor,
validate increasing publication sequence and record provenance, filter only the
requested canonical range while retaining eligible private metadata, apply the
selected publication through the target engine, then persist the resulting
cursor through `SplitRuntime::note_frontiers`/state save. If target apply or
state save fails, return the existing typed failure and leave the prior cursor
durable. If the source page is empty while `applied_through <
source.published_sequence()`, return `SourceHistoryGap`; do not retry forever,
advance the cursor to the source head, or infer completeness from document
history (metadata-only publications and legitimate sequence holes exist).

The cutover gate remains `target_applied_through >= source_high_water` and
`>= acked_through` (`lifecycle.rs:228-235`), followed by the existing writer
fence. No route publication or source cleanup is part of this brief.

### Acceptance cases and checks

1. Backfill completion followed by a crash before the first journal page
   reopens in `CatchingUp`, starts at the explicit persisted boundary, and
   replays the first retained publication through the real engine caller.
2. After one successful page, restart begins strictly after that page's
   persisted cursor; replaying the same operation does not duplicate or lose
   its effect.
3. A page containing only publications outside the moved range still advances
   the source cursor, while selected in-range records and retained non-routing
   private metadata reach the target.
4. Empty source page below a nonzero durable publication boundary returns
   `SourceHistoryGap` and cannot pass `can_cut_over`; target apply failure and
   state-save failure preserve the last durable cursor.
5. Out-of-order/regressed publication sequences and malformed provenance fail
   before target mutation.

Use the existing focused shard supervisor/catch-up selectors and one real
`Engine` restart fixture. Send selectors and source hash to `/root/tb_check`;
do not run Cargo concurrently. Acceptance remains with the coordinator after
review and shared verification.

### Forbidden shortcuts

Do not persist the source head merely because a page is empty, skip a page
because it has no selected range rows, use document-history maximum as the
boundary, weaken sequence/provenance assertions, or claim in-memory cursor
progress as durable restart evidence. Do not add compatibility state versions
or synthetic publications.

### Control and return

Routine implementation is authorized within the listed paths and established
contracts. Escalate if preserving original public revision/session lineage
requires changing snapshot format or split state schema; that is a separate
Astra/coordinator decision. Return changed paths/symbols, exact source identity,
restart/failure evidence, selectors submitted to the build owner, and unresolved
history-retention limits. This brief does not establish cutover readiness.

## Brief B: replication retention frontier failure closure

### Outcome and caller

The real callers are the physical `WalCaptureStep::UpToDate` path and causal
`CausalReceive::Receipt` path in `crates/terrabase-server/src/replication_peers.rs`.
The `replication` SQLite retention consumer may advance only to the minimum
source-journal sequence acknowledged by every authenticated required peer in
the same `(DatabaseRouteScope, placement generation, replica-set epoch)`.
The source value is the verified physical `durable_sequence` or the causal
driver's contiguous `acknowledged_source_cursor`; a causal origin clock,
local engine snapshot head, or remote origin sequence is not interchangeable.

### Existing design to preserve

`ReplicationRetentionFrontier::admit` starts every required peer at zero and
refuses assignment replacement; `acknowledge` rejects unknown peers and only
submits monotonic minima (`replication_frontier.rs:34-101`).
`CausalDriver` advances its source cursor only while contiguous pending entries
are folded and only when `settled.checkpoint` is present
(`production_causal.rs:406-445`); repair entries with `checkpoint=None` must
not move retention. Preserve these contracts.

### Bounded implementation scope

Trace the `required` set supplied at `replication_peers.rs:273-277` back to
accepted assignment construction. If current plumbing proves all physical and
remote causal lanes are registered under the exact admitted scope/epoch, add
only the missing loss/replacement fencing and regression coverage. If it does
not, the worker owns the narrow registration seam in
`production_data_runtime.rs:372-430` and the frontier owner; do not make
`ReplicationProgress` or `durable_peer_frontier` authoritative for retention.
On worker/session loss, retain the required peer at its last safe value (or
zero for a new owner); never remove it so a surviving peer can release history.
A changed assignment/epoch must create a new frontier owner with all peers at
zero. Stale receipts from the previous owner must be rejected.

### Acceptance cases and checks

1. Two required peers: A acknowledges 10 while B is absent; durable frontier
   remains zero. B acknowledges 7; frontier is 7. B then acknowledges 12;
   frontier reaches 10 (and later 12 when A also reaches 12).
2. B drops after 7; A cannot advance beyond 7. A replacement B session starts
   at zero and only fresh authenticated receipt(s) restore progress.
3. A stale receipt from a previous generation/epoch is rejected and cannot
   move a newly admitted frontier. Unknown peer and scope mismatch fail closed.
4. Duplicate/out-of-order causal receipts do not regress or double-submit the
   durable frontier; repair publications do not advance it.
5. A failed `advance_retention_frontier` leaves the candidate retryable and
   does not mark it durably advanced.

Use existing frontier unit tests plus the narrow physical/causal worker seams;
submit selectors/source hash to `/root/tb_check`. No competing Cargo run.

### Forbidden shortcuts and control

Do not derive retention from `durable_peer_frontier`, local snapshot head,
`ReplicationProgress`, current engine head, or a receipt before validation.
Do not remove missing peers, swallow durable submission errors, retry unknown
writes, or broaden this into a replication protocol redesign. A decision to
change public retention semantics, epoch identity, or authorization belongs to
Astra/coordinator. Return changed paths/symbols, exact assignment evidence,
failure/replacement test outcomes, build-owner request, and unresolved
architecture decisions. Worker reports do not establish acceptance.

## Confirmed recurring lesson

- **Cause:** A source journal checkpoint and a causal origin frontier are
  different coordinate systems; using the latter (or local snapshot head) can
  prune history still needed by a peer. Evidence:
  `.agents/replication-retention-caller-next.md:8-12,50-72` and current calls
  in `replication_peers.rs:1038-1060,1080-1091`.
- **Correction:** feed retention only the verified source journal sequence,
  after physical receipt acceptance or contiguous causal fold.
- **Prevention:** every brief/test must include a publication whose origin
  sequence differs from its source journal sequence and prove the durable
  minimum across a missing peer; never label an origin clock as a storage
  retention cursor.

This lesson is confirmed and should be sent to the coordinator for the user
update. The split restart item remains source-backed work; do not call it a
confirmed recurring error until a reproduced failing test or worker evidence
establishes it against the current source.
