# Schema Migrations: versioned evolution with recovery evidence (SM-03)

Status: implemented for the engine seam; focused in-process evidence only.
Ticket: TerraScale/TerraBaseV2#27 (SM-03). Binding decisions: charter
PD-2026-08-26-04 D3, ADR 0007 D-3, and this record. Date: 2026-08-26.

This is the migration-style decision record ADR 0007 D-3 and the ticket
leave to SM-03 ("declarative transforms first, programmatic escape only
through typed operations, decided in-ticket"), the documentation of the
migration model, the rollback-limit statement, and the focused kill-point
evidence. It makes no recovery, replication, multi-region, or performance
claim; the exclusive recovery lane owns the formal Recovery Receipts.

## Decision: the migration style

**Declarative-first is affirmed.** A Schema Migration is one typed contract
— [`SchemaMigration`] in `src/families/migrations/` — carrying a collection,
a from-version, a to-version (`to > from`, forward-only), a target schema
whose version identity equals `to`, and an ordered list of at most 256
declarative field transforms from a closed vocabulary:

| Transform | Coercion contract | Rollback |
| --- | --- | --- |
| `AddField { field, default }` | absent — write the typed default; present — keep the stored value (add-if-absent, never overwrite) | not reversible: a pre-existing value equal to the default cannot be distinguished from a defaulted one |
| `RemoveField { field }` | present — remove the field and its stored values; absent — nothing to remove | not reversible: the stored values are dropped |
| `RenameField { from, to }` | source present and destination absent — move the value; source absent — skip; destination present — reject (`TargetFieldOccupied`), never overwrite | reversible: the reverse rename restores it exactly |
| `RetypeField { field, coercion }` | value addressed by the closed coercion — convert, rejecting every value outside the coercion's explicit domain; any other shape — pass through unchanged | reversible: the two exact coercion pairs invert |

The closed coercion set is `IntegerToText` / `TextToInteger` (canonical
signed-decimal renderings only — `-0`, `+1`, `01`, whitespace, and floats
are refused) and `IntegerToNumber` / `NumberToInteger` (exact-domain only —
a widening past the exactly representable integer range of the float domain
and a fractional or non-finite narrowing are refused). The two pairs are
exact inverses of each other; that exactness is what makes a reversible
migration's rollback lossless. Every transform is idempotent on its own
result, which is what makes page application safe to re-serve.

Evidence for the decision:

- The dry run and the evidence contract require the migration to be
  inspectable *before* it runs: the plan states per-document outcomes,
  counts, and checksums. That is only possible over a closed, interpretable
  vocabulary; over arbitrary code it is impossible to state what will
  happen without running it.
- The house rule against arbitrary code execution in engine-evaluated
  languages (charter PD-2026-08-26-08 for TQL; ADR 0007 D-4's typed-only
  exposure) extends naturally to migrations: an engine seam that embeds an
  interpreter embeds an attack surface and an unbounded work contract.
- Bounded and deterministic by construction: at most 256 transforms per
  contract, each doing bounded map operations on one document value, so a
  page's work is linear in a bounded page (1024 documents,
  `MAX_MIGRATION_PAGE`, the replication catch-up bound).

### The programmatic escape is the typed document seam

A caller that needs logic beyond the vocabulary — computed defaults,
conditional reshapes, cross-collection moves — performs it through the
engine's typed document writes: read through a snapshot, compute at the
host, write through `Transaction::put`, which enforces whatever schema the
collection records at that moment. That path is the sanctioned escape: it
is typed, tenant-scoped, audited by the engine's ordinary commit machinery,
and outside the migration contract — it carries no migration receipt and
moves no version identity. What is rejected is the *engine-embedded*
programmatic migration: no closures, no function pointers, no expression
interpreter inside the migration engine.

### Rejected alternatives

- **A callback/program transform** (`Box<dyn Fn(&mut Document)>`): makes
  dry-run evidence, bounded work, determinism, and rollback undeclarable;
  executes unbounded host code inside the engine; incompatible with the
  no-code-execution rule the query language already follows.
- **An embedded expression language** (mini-DSL for computed values): a
  second language to version, bound, and audit; the closed vocabulary plus
  the typed document seam covers the same needs without one.
- **Migration as a Logical Migration instance**: rejected by ADR 0007 D-3
  — different source (a live collection, not a V1 export), different
  contract (version evolution, not format transfer), different evidence.
  The glossary keeps Schema Migration and Logical Migration separate, and
  this module shares no terminology or code with `src/migration/`.
- **Whole-collection single-transaction migration**: atomic but with
  unbounded transaction size (the whole collection must stage in one
  transaction) and no observable, resumable step boundaries; the paged
  drive keeps the in-flight window bounded and the recovery story real.

## The model

- A migration moves one collection from its recorded
  [`SchemaVersion`] to the contract's `to` under the recorded schema's
  forward-only rule: finalize seats exactly the version advance SM-02's
  `validation_stage` would allow (`to > from`), plus the transforms and
  the evidence. A direct family `ValidationWrite` declaring a greater
  version remains possible and is a plain declaration (no documents are
  touched, no receipt exists); a migration is the sanctioned way to
  advance a version *while transforming documents*, and it is the only way
  that produces receipt evidence.
- Drive state is a reserved record at `family:migration/<collection>`:
  the manifest digest of the driving contract, the state
  (`in-progress`, `rolling-back`, or `complete` with the receipt), the
  composite scan cursor, the running document counts, the per-kind
  transform counts, and the running SHA-256 chains. The record is tenant-
  scoped by the engine's document keying, like every family record.
- The drive is stepped by typed operations the host drives, in the SH-03
  pattern: `dry_run` (never-committed read transaction, nothing staged),
  `begin`, `apply_page` (one bounded page = one committed transaction at
  `LocalDurability::Synced`), `finalize` (verify, then seat), `rollback`
  (one bounded page per call). `apply` is a convenience loop. Every step
  resolves an uncertain commit through the durable operation ledger
  (`Engine::resolve_commit`) and is safe to retry: a resolved-as-committed
  step advanced the recorded cursor; an unresolved one is retried against
  the same recorded state; transforms are idempotent on their own results.
- Writes during a drive are fenced to the target: the engine write-path
  schema hook (`enforce_put`, the SM-03 evolution hook in
  `src/families/schemas.rs`) enforces the migration's target schema while
  the record reads `in-progress`, and falls through to the still-recorded
  from-version schema while it reads `rolling-back` — including the
  completing rollback page, whose record clear is staged ahead of the
  restoring puts and reads as absent to the hook (the hook reads through
  the transaction, and staging the record write and its clear in one
  transaction would target the same record twice). A collection is never
  unvalidated and never validated against both versions at once. The
  coordination boundary matches SH-03's: the host quiesces or writes
  target-conforming documents; the engine validates and executes. Like
  SM-02, an occupied reserved record address fails closed
  (`StorageFailure` at the engine vocabulary, a recorded mapping limit),
  never reading as an absent record.
- The scan pages the tenant's documents through the engine's bounded
  snapshot window (`snapshot_documents_from`) with a composite
  (collection, identifier) cursor at the migration layer, because the
  storage resume point is identifier-only and identifiers may repeat
  across collections. Re-served keys are duplicates only; a repeated
  cursor position fails closed (`PageProgressStalled`) instead of
  looping.

## Evidence in the recovery-lane vocabulary

- **Manifest**: the SHA-256 manifest digest over the contract's identity —
  collection, versions, target schema record, and canonical transform
  encodings (text defaults digest length *and* bytes). Two drives whose
  contracts digest equally are the same migration; any changed transform,
  default, or target rule changes the digest. Idempotency, resume, and
  conflict refusal all compare manifests.
- **Checksums**: a SHA-256 chain over the collection's documents in
  canonical key order, linked per document over its identity and a
  canonical value encoding, with a domain separator per side (`source` /
  `result`). The running chain is bounded state (32 bytes + a count) and
  is recorded in the drive record, so the chain accumulates correctly
  across pages and across an interrupted-and-resumed run.
- **Counts**: documents scanned, documents transformed, per-kind
  transform applications (added / removed / renamed / retyped), and pages
  committed.
- **Fail-closed comparison**: `finalize` re-derives the result chain from
  storage at its own snapshot and compares it against both the recorded
  chains and the dry run's stated plan — count or checksum mismatch, or a
  per-kind count mismatch, refuses with `CountMismatch` /
  `ChecksumMismatch` and seats nothing. A collection that changed between
  the dry run and the finalize is caught by exactly this comparison.
- **Receipt**: the completed record stores the receipt — manifest, source
  and result digests, counts, pages, the rollback limit in force, and
  whether a not-rollbackable migration ran under explicit acknowledgment.
  It is secret-safe by construction: digests and counts only, no document
  bodies, field values, paths, or credentials.

## Rollback limits

Rollback exists only before `finalize` seats the version advance. The
rollback-limit statement is computed from the transform list, carried on
the contract and the receipt, and enforced:

| Contract contains | `rollback_limit()` | Rollback behavior |
| --- | --- | --- |
| only `RenameField` / `RetypeField` | `Reversible` | `rollback` applies the exact inverse list (reversed, directions swapped) page by page; on completion the record is cleared and the collection enforces its from-version schema again |
| any `AddField` or `RemoveField` | `NotReversible { causes }` (`FieldDefaulted`, `FieldRemoved`) | `begin` refuses without `MigrationOptions::acknowledge_not_rollbackable` (`AcknowledgmentRequired`); `rollback` refuses (`NotRollbackable`) |

What a reverse migration cannot restore, stated:

1. **Removed field values** (`RemoveField`): the values are dropped at
   apply time; no inverse exists.
2. **Defaulted-field provenance** (`AddField`): a stored value equal to
   the default is indistinguishable from a defaulted one, so removing the
   field could drop pre-migration data.
3. **Writes that arrived during the drive**: concurrent target-conforming
   puts pass the fence and are not the migration's to restore; an inverse
   transform that would overwrite a re-occupied destination fails closed
   (`TransformRejected`) rather than losing data.
4. **A seated version advance**: rollback after `finalize` is refused
   (`NotInProgress`) — version identity never moves backward; the forward
   path is a fresh migration.

## Kill-point evidence (focused, in-process)

Lane statement: these runs are focused in-process tests against real
durable engines (temporary directories, real publication journals and
operation ledgers), with interruptions modeled as step boundaries — the
host stops calling the next step, the engine is dropped, and the drive
resumes from the durable record. **The exclusive recovery lane is not
taken here**; per the wave's lane discipline the formal Recovery Receipts
for schema migrations ride with the recovery lane (BK-02's packet owns the
lane), and this record is the input evidence. No fault-injected process
kill, no power-loss simulation, and no performance claim is made.

| Kill point | Test (`src/families/migrations/tests.rs`) | Outcome asserted |
| --- | --- | --- |
| K1: after `begin`, before any page | `interrupted_migrations_resume_from_the_recorded_step` | drop + reopen; re-`begin` is idempotent; the drive completes; the resumed receipt equals an uninterrupted twin's manifest, counts, digests, and pages |
| K2: mid-collection, after a committed page (collection > `MAX_MIGRATION_PAGE`) | `interrupted_pages_resume_without_double_application` | page 1 commits 1024 documents; drop + reopen; resume completes; documents counted exactly once across the interruption; boundary documents of both pages carry the coerced value exactly once; the in-progress fence accepts a target-conforming put and rejects a target-violating one |
| K3: after the scan completed, before `finalize` | `interruptions_after_the_scan_and_after_finalize_are_idempotent` | drop + reopen; `finalize` seats; repeated page/step calls answer idempotently |
| K4: after `finalize` | same test | every later step is a no-op with the recorded receipt; the storage sequence does not move |
| Mid-rollback | `rollback_before_finalize_resumes_across_pages` | drop + reopen between rollback pages; the resumed rollback restores exactly the migrated documents; the collection returns to its from-version values and schema; the record is cleared |
| Evidence tamper | `evidence_mismatches_fail_closed` | a forged plan count and a real post-plan collection change both refuse at `finalize` with typed mismatches; nothing seats; the drive stays resumable and seats after repair |

Red-then-green record: before the SM-03 evolution hook in
`src/families/schemas.rs` landed (the write-path retarget to the
migration's target schema), the focused suite failed genuinely —
`versions_advance_forward_only_through_the_recorded_lineage` and
`interrupted_pages_resume_without_double_application` aborted with
`TargetViolation` at their first transformed-document put, because the
write path enforced the from-version schema against the migration's own
result. With the hook, all 23 in-crate migration tests and 18 families
integration tests pass.

Repair red-then-green (warden loop 1): a rollback completing in one page
— a collection no larger than `MAX_MIGRATION_PAGE` rolled back straight
from the forward drive's in-progress record — ran its restoring puts
while the committed record still read `in-progress`, so the hook fenced
from-version-shaped restored documents against the target and the
rollback spuriously refused with `TargetViolation`
(`single_page_rollback_restores_documents_the_target_rules_out` was red
for exactly that). The completing page now stages the record's clear
before the puts, which reads as absent to the hook and fences the puts
to the from-version schema; the restoring puts and the clear commit
atomically. 24 in-crate migration tests pass.

## Terminology and parity

Schema Migration (this module) and Logical Migration (`src/migration/`,
WS-BACKUP/BK-03) are distinct concepts per CONTEXT.md and ADR 0007 D-3; no
terminology is shared and no code is. PB-5012 (storage format upgrade) is
again untouched by this ticket. Schema-related parity rows keep the
proposed dispositions recorded in `docs/families/schemas.md` (SM-02); this
ticket adds no overlay mutation — WS-PARITY owns every disposition.

## Tests (all through public seams)

In-crate (`src/families/migrations/tests.rs`), engine-driving:
`contracts_validate_versions_targets_and_names`,
`transform_coercion_contracts_are_explicit_and_idempotent`,
`rollback_limits_are_computed_from_the_transform_list`,
`dry_run_states_the_plan_without_mutating_anything` (storage sequence,
document values, record absence, and enforced version all unchanged),
`apply_moves_the_collection_between_versions_with_evidence`,
`re_running_a_completed_migration_is_a_noop_with_receipt`,
`versions_advance_forward_only_through_the_recorded_lineage`, the five
kill-point and resume tests above, `finalize_refuses_before_the_scan_completes`,
`evidence_mismatches_fail_closed`, `reserved_record_collisions_fail_closed`,
`rollback_restores_a_reversible_migration_and_states_its_limits`,
`single_page_rollback_restores_documents_the_target_rules_out`,
`not_rollbackable_migrations_require_explicit_acknowledgment`,
`migrations_are_tenant_scoped` (cross-tenant and cross-engine probes),
`an_empty_collection_migrates_with_zero_evidence`,
`the_scan_cursor_is_composite_and_exact` (identifier reuse across
collections), and `the_record_codec_round_trips_exactly`. The digest
module carries its own FIPS 180-4 known-answer and streaming tests.

Integration (`tests/families/migrations_public.rs`), no engine handle:
contract validation, the coercion contracts, the rollback-limit
computation, bounds, and contract identity sensitivity.

## Limits and risks

- **The exclusive recovery lane is not taken.** The kill points are step
  boundaries, not injected process kills; crash-restart qualification of
  the migration drive belongs to the recovery lane, and this record is
  its input (collision action recorded for the parent).
- **The write-path hook now reads one more reserved record per put**: a
  schema-free collection's put performs the absent-record lookups of both
  the validation record (SM-02) and the migration record (SM-03).
  Per PD-2026-08-26-03 this is performance-relevant: benchmark validation
  is **unrun** and no performance statement is made here. A combined
  lookup or cache is a later, benchmark-gated decision.
- **Concurrent writes during a drive are the host's coordination
  boundary** (as in SH-03): the engine fences puts to the target schema
  but does not quiesce the collection; target-conforming writes enter and
  are the host's to account for. A concurrent *direct* schema declaration
  (family `ValidationWrite`) during a drive is refused at finalize
  (`FromVersionMismatch`), never silently overwritten; refusing it at
  stage time would need a `src/families/store.rs` edit outside this
  packet's scope (collision action recorded).
- **One migration record per collection**: a completed record is
  superseded by the next migration's `begin` (the previous receipt is
  replaced); there is no receipt history chain. A receipt log is a later
  surface decision (SM-04 reads the current record).
- **Rollback of large collections costs a full re-scan** (the inverse
  scan re-reads the collection). Bounded per page; no efficiency claim.
- The migration record, like every family record, shares the reserved
  collection's collision semantics: a user document occupying the record
  address makes the collection's migration state unreadable and every put
  to the collection fails closed (probe-tested).
