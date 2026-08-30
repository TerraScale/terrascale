# Deletion workflow lane (domain-only receipt)

Status: recorded 2026-08-29, landing with this commit on `develop`.
Gap-sweep row 40 (`docs/evidence/qualification-gap-sweep.md`): *"deletion
with active Regions and backup"* was **missing** — no `DeletionWorkflow`
code, no thirty/ninety-day constants, no legal holds anywhere in the
engine. This lane lands the typed domain module. It does **not** close the
row; see "Honest limits".

Focused check (the only gate run for this lane; `scripts/check.sh` belongs
to CI):

```text
cargo test -p terrabase-v2 --lib deletion::
    18 passed; 0 failed; 1105 filtered out
cargo clippy -p terrabase-v2 --lib        # zero findings in src/deletion/
```

## What exists now (`src/deletion/`)

A pure domain module in the `src/usage/` shape — typed facts, typed
errors, idempotent indexes, canonical checksummed encoding, no storage,
WAL, transport, TBEP, or Dashboard dependency. Files:
`src/deletion/mod.rs` (domain), `src/deletion/tests.rs` (unit + property
tests), registered by one `pub mod deletion;` line in `src/lib.rs`.

Charter requirements mapped to code (`GOAL.md` "Compliance and retention
baseline", scenario row "deletion with active Regions and backup", risk
row "Deletion misses a Region, index, cache, or backup", and AC-19):

- **Globally durable workflow** — `DeletionWorkflow` walks
  `Requested -> Admitted -> Executing -> Verified -> Complete` (`Failed`
  the only other terminal state). Admission is `GlobalDurable`-shaped:
  every selected active Region must have durably recorded the request
  (`GlobalDurableAdmission`, superset-of-selected acknowledgement,
  `AdmissionNotGlobalDurable` / `UnknownRegion` fail closed), and the
  admitted plan must contain a live-copy target for every selected Region
  (`PlanMissesRegion`). A Region added later cannot be pulled into an
  admitted workflow — that is the "adding a Region cannot silently move
  regulated data into it" rule at domain level.
- **Per-copy receipts** — one `CopyReceipt` per planned `CopyTarget` over
  the classes `LiveRegion`, `Index`, `Cache`, `DerivedView`,
  `BackupRotation`. Backup copies receipt `RotatedOut` (normal rotation);
  live classes receipt `Removed`. Receipts carry identity, outcome, and
  timestamp only — no document content, personal datum, path, or secret.
- **Thirty-day live deadline** — `LIVE_DEADLINE_DAYS = 30` /
  `LIVE_DEADLINE_MILLIS` for live data, indexes, caches, derived views,
  and active-region copies.
- **Ninety-day backup expiry** — `BACKUP_DEADLINE_DAYS = 90` /
  `BACKUP_DEADLINE_MILLIS` for immutable backup rotation.
- **Explicit legal holds** — `HoldRecord` (`Legal`, `Security`, `Payment`,
  `Tax`, `Fraud`) records the authority reference, recording time, expiry,
  and the exact retained-field set ("retain only the required fields").
  While a hold is unexpired it blocks verification
  (`HoldBlocksCompletion`), blocks completion, and `ordinary_access_blocked`
  / `retained_fields` expose the blocked state and the only surviving
  fields. Ending a hold (release or expiry) requires re-execution: receipts
  predating the hold's end are refused (`ReceiptPredatesHoldEnd`), because
  the retained fields were not deleted during the hold.
- **Deadline arithmetic with hold coverage** — a copy's effective deadline
  is `requested_at + class deadline + hold coverage` where coverage is the
  union (never the sum) of hold windows clipped to the receipt time —
  AC-19's "unless a recorded legal hold applies" as exact arithmetic.
- **Honest failure** — a crossed deadline fails the workflow terminally
  (`Failed(DeadlineExceeded)`) with the violations still queryable
  (`deadline_violations_at`); it is never a silent success.
- **Idempotent resume** — receipts are idempotent and monotone (exact
  duplicate and stale replay are no-ops; later receipts supersede), the
  ledger is idempotent by request identity over canonical bytes
  (`DeletionLedger`, `WorkflowConflict` on reuse with different facts),
  and the workflow encodes/decodes canonically with a domain-separated
  BLAKE3 checksum as the restart interface.

## The test lane (18 rows, all in `src/deletion/tests.rs`)

Deterministic, injected-time only; the "property" rows are exhaustive
loops over every hold kind, every copy class, and every resume prefix:

- `charter_deadline_constants_are_thirty_and_ninety_days` — constants and
  per-class deadlines.
- `request_requires_selected_regions_and_a_prior_verification`
- `happy_path_walks_every_charter_state_in_order`
- `admission_must_be_global_durable_over_every_selected_region` — partial
  acknowledgement, foreign Region, early admission time, plan missing a
  Region's live copy, plan naming an unselected Region, empty plan, double
  admission.
- `no_execution_precedes_admission`
- `receipts_are_idempotent_monotone_and_plan_bound`
- `verification_requires_every_planned_copy`
- `every_hold_kind_blocks_verification_and_ordinary_access` — all five
  charter hold families.
- `expired_holds_stop_blocking_but_receipts_must_postdate_the_hold`
- `early_hold_release_ends_at_release_time_and_extends_deadlines` — same
  receipts violate without the hold and pass with it.
- `hold_records_validate_their_window_and_retained_fields`
- `overlapping_holds_count_once_in_coverage`
- `deadline_enforcement_is_exact_and_terminal_on_violation` — every class,
  to the millisecond at the boundary and one millisecond over.
- `terminal_states_refuse_every_mutation`
- `resume_from_encoded_bytes_at_every_step_matches_a_continuous_run` —
  decode at *every* step prefix, replay recorded receipts, converge to the
  continuous run's exact canonical bytes.
- `encoding_fails_closed_on_corruption` — truncation and bit flips.
- `ledger_is_idempotent_by_request_identity`
- `retained_fields_union_across_concurrent_holds`

## Honest limits (why row 40 stays partial, not covered)

- **Domain only.** Nothing in the engine *executes* a deletion workflow
  yet: no storage-tier, index, cache, view, or backup-rotation path
  creates a `CopyReceipt`; no WAL/operation-ledger persists a
  `DeletionWorkflow` (the canonical encoding is the intended durable
  shape, and decode re-validates every invariant); no ADR 0028 durable
  operation resource, TBEP operation, or public SDK surface starts or
  observes one. Wiring is later work in those modules.
- **Receipts are caller-attested facts.** The domain cannot prove a copy
  is physically gone; it proves a coherent, plan-bound, deadline-checked,
  idempotent record of claims. Real enforcement needs per-tier execution
  lanes (storage compaction, index drops, cache eviction, view
  invalidation, and the S3 lifecycle policy selected under ADR 0037, which
  today has no hook that receipts a deletion's backup-rotation copy).
- **"Block ordinary access" is a domain predicate**
  (`ordinary_access_blocked`), not an authorization-plane change; no
  policy generation, field projection, or read path consults it.
- **Pseudonymization is recorded, not performed.** Payment/usage records
  surviving a hold declare their stable pseudonymous accounting
  identifiers as the retained-field set; the domain does not rewrite
  profile fields — that is storage-layer work.
- **Synthetic Region codes.** Tests use `gru`/`sfo`/`fra`-shaped
  identifiers through the existing `RegionId`; the production `RegionCode`
  catalog (gap row 35) does not exist in the engine yet.
- **No timing, production, or WAN claim is made.** The thirty-day and
  ninety-day deadlines are enforced against caller-supplied times in a
  deterministic in-process lane. Whether live regions and immutable
  backups actually meet them in production is unproven and belongs to the
  integration and fault lanes that follow.

## What closes row 40

Per-copy execution integrated with the real tiers and the backup
scheduler, workflow records persisted through the engine's durable paths
with restart/fault lanes against real storage (not just decode-equality),
the ADR 0028 operation resource and public contract surface, and an
authorization-plane consult of holds. None of that exists today; this
module is the typed contract those lanes will drive.
