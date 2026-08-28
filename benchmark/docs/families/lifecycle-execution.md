# Lifecycle and archival execution record (GAP-F06, TerraBaseV2#75)

Status: **implementation record with proposed decisions**. Everything under
this packet's write scope landed and is proved by focused tests. The
decisions marked **PROPOSED** await parent ratification. No parity row was
flipped, and no Proof ID was added to any row by this packet.

Owner: GAP-F06 (#75), workstream GAP-F. Parity rows touched conceptually:
PB-3033 (`expiration-and-refresh`, unresolved), PB-3037-area data archival
(`data-archival`, unresolved) — both remain unresolved with empty Proof ID
lists; this record adds coverage notes only.

Worktree branch `fleet/gapf06-2`, base `da0df43`. Pinned V1 citations in
this record are against the read-only V1 clone at
`fa99b9257776dbe625e6a2388c74c9ce916637ee` (paths relative to that clone's
root).

## What landed

`src/families/lifecycle.rs` (the module root wired by the integrator at
`da0df43`) now carries the execution half of the lifecycle and archival
families, exposed as three inherent `FamilyStore` methods — the same
pattern GAP-F01 used to add the attachment content plane without editing
`src/families/mod.rs`:

- `FamilyStore::plan_lifecycle_pass(&TenantScope) -> LifecyclePass` — one
  read-only pass plan from one immutable snapshot and one pass time. The
  plan enumerates the tenant's family records through the landed W3-F
  `FamilyStore::snapshot_family_records` materialization (the same seam
  backup consumes, integrator action W4-CA-5) and keeps only lifecycle
  and archival records; owner presence is then a point read inside one
  never-committed read transaction. (Repair loop 1 replaced the original
  windowed paging here — see the repair section for the cycle it admitted
  and the memory profile this trades to.)
- `FamilyStore::stage_lifecycle_pass(&mut Transaction, &LifecyclePass) ->
  LifecyclePassStaged` — stages every planned action into the caller's
  transaction. Nothing is durable until the driver commits.
- `FamilyStore::read_document_visibility(FamilyReadContext, VisibilityRead)
  -> VisibilityReport` — the batch visibility read: one entry per requested
  document, exactly one of `Visible` / `Expired` / `Missing`, observed at
  one snapshot.

Actions the pass plans and stages:

1. **Expiry deletion.** A document whose lifecycle record's `expires_at`
   has elapsed is deleted (`expires_at <= now`, the same predicate the
   landed `LifecycleRecord::is_expired` and the W3-F lifecycle read use).
   The staged unit per document is: the document delete
   (`ExpectedVersion::Exact` of the version observed inside the staging
   transaction), the lifecycle record clear, and the lifecycle family
   tombstone (`TombstoneCommand::Record`'s encoding, written through the
   crate-private record seam) fencing stale lifecycle writers.
2. **Refresh consumption.** A due `refresh_at` is set to `None`; the
   expiry is untouched. If both targets become `None`, the record clears —
   the same write-or-clear shape the landed stage functions use.
3. **Archival schedule materialization.** An archival policy
   (`after_ms` set, no `at`, not archived) gets `at = now + after_ms`
   recorded durably into the archival record (saturating addition).
4. **Archival marks.** A record whose `at` is due (`now >= at`, not
   archived) is marked `archived = true` with `at` kept as the archival
   time. V1 contrast (recorded for the parity sweep): V1 *removes* the
   `@archive-at` trigger when it sets `@archived` (DataArchivalStorage.cs:40-42);
   V2 keeps `at` because the landed archival vocabulary freezes the
   schedule time in the record (`ArchivalCommand::Archive { at }` rounds
   on the same shape) — a deliberate frozen-vocabulary consequence, not an
   oversight.
5. **Stale record cleanup.** A lifecycle or archival record whose owner
   document is gone is cleared (the owner must still be gone at stage
   time).

Every stage action re-verifies its facts inside the staging transaction
before staging anything; a document whose facts no longer match the plan is
reported in `LifecyclePassStaged::skipped` and left for the next pass.

New tests: 13 inline in `src/families/lifecycle.rs`
(`#[cfg(test)] mod tests` — 10 from the original loop, 3 from repair loop
1), 2 integration in
`tests/families/lifecycle_execution_public.rs` (public construction and
accessor observation only — integration tests cannot seed documents, since
`TenantScope` has no public constructor by design).

## D1 — Execution-owner decision (PROPOSED)

**Question.** Who executes expiry/archival maintenance: an engine tick in
the `Engine::maintain_indexes` shape, or a host-scheduled drive?

**Landed answer (PROPOSED for ratification).** The pass *body* is
engine-owned family behavior; the *cadence* and the *commit* belong to the
host driver, split over the family seam's two existing operations:
`plan_lifecycle_pass` (the read) and `stage_lifecycle_pass` (the write into
the driver's transaction). The host reads a plan, begins a transaction,
stages the pass, and commits — exactly the caller-commits discipline every
other family write follows (`docs/families/w3-families.md`: "family code
never commits on its own"; one transaction marker per commit).

Evidence and alternatives considered:

- **IX-03's `Engine::maintain_indexes` (src/engine/mod.rs:969-979)** is the
  in-repo precedent for engine-owned maintenance: "the engine-owned body a
  host schedules on its own timer (PD-2026-08-26-01 D1: cadence belongs to
  the host side, the pass to the Engine)". GAP-S1's `Engine::compact`
  (src/engine/mod.rs:981-999) repeats the split. This packet keeps the
  identical ownership split but cannot add the `Engine` method:
  `src/engine/**` is outside this packet's write scope.
- **An `Engine::maintain_lifecycle` wrapper now** would begin and commit
  the pass's transaction inside the engine seam, exactly like
  `maintain_indexes` runs its pass. Rejected *for this packet* as a
  write-scope violation; recorded below as a proposed collision action (a
  one-line delegating method) for the parent to ratify or drop. Nothing in
  the landed API depends on it: a host calls
  `plan_lifecycle_pass` + `stage_lifecycle_pass` directly.
- **A background thread inside the engine** (V1's shape:
  `BackgroundWorkBase` with `WaitOrThrowOperationCanceled`) was rejected
  for V2's engine: the engine core has no wall clock in its seams and no
  timer facility; the charter's lane discipline keeps time-driven behavior
  caller-driven (the same reasoning GAP-R02 recorded when rejecting
  time-based tombstone retention, `docs/replication/tombstone-retention-watermark.md`
  "Rejected alternatives").
- **Host-scheduled everything (no engine-owned body)** was rejected: the
  deletion semantics (tombstone recording, guard order, version fencing)
  are family contract, not host policy; a host reimplementing them would
  fork the invariant.
- **V1 evidence for the body being server-owned**:
  `ExpiredDocumentsCleaner` (Expiration/ExpiredDocumentsCleaner.cs:19-45)
  and `DataArchivist` (DataArchival/DataArchivist.cs:24-33) run inside the
  V1 server on 60-second default cadences
  (ExpiredDocumentsCleaner.cs:21-23), batching 4096 documents per run
  (ExpiredDocumentsCleaner.cs:25-27). V2's equivalent batching knob is the
  driver's commit granularity (a host can stage and commit one plan slice
  at a time); a per-pass document-count bound is a deferred item below.

**A pass that stages nothing publishes nothing**: the engine rejects an
empty write set (`CommitResult::Rejected(Validation)`,
src/storage/file.rs:332-334), so a driver must drop the transaction when
the staged receipt's action lists are all empty. The inline test fixture
demonstrates the rule; it is documented here because a host that forgets
it will see typed rejections, not silent no-ops.

## D2 — Deletion evidence: GAP-R02 interplay (landed behavior, cited)

The pass's deletions are ordinary journal delete publications through the
Engine transaction seam — the same seam a caller's delete rides. They
therefore inherit the landed GAP-R02 rules
(`docs/replication/tombstone-retention-watermark.md`) without any
lifecycle-specific mechanism:

- Each delete is a journaled `Delete` publication (the record's "Problem"
  section, lines 18-23), so the tombstone retention watermark governs how
  long its deletion evidence survives (D1's retention invariant), a
  backup chain below the history bound archives the retained `Delete`
  records of the window (D4), and a restored chain replays them "through
  the same engine transaction seam as journal deletes, so a restore below
  the bound removes exactly the documents the source removed" (D4, backup
  chain bullet).
- The pass records the **lifecycle family tombstone** for each deleted
  document (`family:tombstones/<document>`, the W3-F guard record) with
  the deletion version, so the landed guard order
  (`src/families/store.rs` `guard_document`: tombstone newer than the
  writer's observed version rejects first) fences a stale lifecycle writer
  from resurrecting lifecycle state. This mirrors W3-F's recorded purpose:
  "The tombstone family is the W3-F mechanism to record and guard such
  deletions" (`docs/families/w3-families.md`, Limits).
- The pass does **not** drive `TombstoneRetention` itself, does not
  declare history bounds, and does not register consumer floors; those are
  the landed GAP-S1 driver's (`Engine::compact_with_retention`,
  src/engine/mod.rs:1027+) and the backup run's roles respectively.

Test evidence: `expired_documents_are_deleted_by_the_pass_and_record_evidence`
(tombstone observable through the public `Tombstones` read) and the two
backup-interaction tests below.

## D3 — Archival policy anchor (PROPOSED)

The landed archival vocabulary (`ArchivalCommand::{SetPolicy{after_ms},
Archive{at}, Restore}` in `src/families/payloads.rs`) records a delay
policy with **no anchor**: nothing in the frozen record shape says when
the delay starts. V1 has no policy form at all — its archival trigger is
an absolute per-document time the client writes (`@archive-at`,
Constants.cs:518; executed by DataArchivalStorage.cs:22-47: at the due
time set `@archived` true and remove `@archive-at`).

**Landed answer (PROPOSED).** The pass materializes the schedule on its
first observation: `at = pass_now + after_ms`, recorded durably in the
archival record; later passes archive when `now >= at`. Consequences: the
delay runs from the first pass that sees the policy, not from the exact
policy write (bounded by the host's cadence — V1's own default cadence is
60 s, DataArchivist.cs:32, so the granularity matches V1's scheduling
granularity); the schedule is durable across restarts; and the explicit
`Archive { at }` command remains the caller's immediate, anchor-free mark.

Alternatives: anchoring to the policy write time requires a recorded
set-time the frozen `ArchivalRecord` does not carry (a `state.rs`/payload
change — collision territory); anchoring to a process-local first-seen map
is not restart-safe; executing `SetPolicy` by immediately writing `at`
inside the landed stage would change the frozen stage's observable
behavior. A public payload shape for "schedule archival at" (an explicit
`Schedule { at }` command) is listed under proposed collision actions as
the cleaner long-term home for this mechanism.

## D4 — Refresh semantics versus the brief's phrasing (honesty note)

The dispatch brief describes the deliverable as "refresh writes extend
expiry per V1 semantics". **Verified against the pin, V1 refresh does not
extend expiry.** At `fa99b925`, refresh processing removes the `@refresh`
marker and re-puts the document with the marker removed
(Refresh/RefreshStorage.cs:24-57, marker removal at lines 34-42); the
`@expires` metadata passes through the re-put untouched. A repo-wide grep
for extend/sliding expiry semantics finds none; the only other `@refresh`
consumers are subscription filtering (SubscriptionConnection.cs:378) and
conflict comparison (DocumentCompare.cs:127). GAP-F06 implements the pin:
refresh execution consumes the marker and leaves expiry untouched, and the
landed W3-F behavior ("a refresh does not clear an elapsed expiry",
`expiration_and_refresh_updates_do_not_resurrect_deleted_documents`)
stands unchanged. The "extend expiry on refresh" variant (a sliding
expiration) is **not implemented and not claimed**; if the parent wants it,
it is a new semantic to ratify, not a V1 parity item at this pin.

Ordering decision: expiry outranks refresh and archival on the same
document (the plan classifies expiry first; the stage deletes first). V1
runs its expiration and refresh workers concurrently over the same queue
(ExpiredDocumentsCleaner.cs:93-99); a document deleted by the expiry pass
drops out of the refresh queue because its queue entry resolves to a
missing document (DocumentBackgroundWorkStorage.cs:16-20). V2 makes that
implicit resolution an explicit ordering.

## D5 — Read filtering and "no partial-expiry states" (landed)

`read_document_visibility` answers each requested document with exactly
one state from one snapshot: present-and-unexpired is `Visible`,
present-and-expired is `Expired`, absent is `Missing`. An expired document
is therefore not visible **before** any maintenance deletes it — there is
no window in which the lifecycle lens shows the same document as both
alive and doomed, and no partial state in which the expiry is half-applied
(the stage's delete + record clear + tombstone ride one transaction, so
there is no intermediate observable state at deletion either).

Scope honesty: this filters **through the lifecycle lens** — the read this
module owns. The plain Engine document read (`Transaction::read` /
`Engine::read`) still returns an expired-but-not-yet-deleted document;
making the engine seam itself expiry-aware is engine territory (the
engine does not consult family state — the recorded W3-F limit that
schema validation at `Transaction::put` only partially closed). The
red-then-green proof: the red suite pinned `Expired` for the
expired-but-undeleted document against a baseline that answered `Visible`;
the green run returned the three-state contract. Callers that must not
see expired documents read through `read_document_visibility` (or run the
pass, after which the document is absent at every seam).

V1 contrast recorded: V1 leaves an expired document readable until the
cleaner deletes it (the V1 read path has no expiry filter; the W3-F test
`CanAddEntityWithExpiry_ThenReadItBeforeItExpires_ButWillNotBeAbleToReadItAfterExpiry`
in test/TerraBase.SlowTests/Server/Documents/Expiration/ExpirationTests.cs:49-124
reads the document before advancing time and asserts absence only after
the cleaner ran). V2's visibility read is deliberately stronger and
addresses PB-3033's named risk ("Expiry races can expose deleted data").

## Archival and backup/restore interaction (BK wave, landed and tested)

What the landed BK wave does with lifecycle/archival state, and what this
packet proves about it:

- Family records — including `family:lifecycle/*` and `family:archival/*`
  — are documents of the reserved collection; a full or snapshot backup
  archives them as state and counts them separately
  (src/backup/mod.rs:120-129, manifest `family_records`), and a restore
  re-puts them through the Engine transaction seam
  (src/backup/restore/mod.rs:26-33). An archived document is retained
  state, not deleted state, so it restores.
- The pass's deletions are journal delete publications: an incremental
  link over the deletion boundary carries them (manifest `deletes`), and
  restoring the chain applies them — GAP-R02 D4's restore rule, exercised
  here through the public `run_backup` / `RestorePlan::restore_latest`
  seam (BK-01 `evidence/recovery/bk01-focused-runs.md`, BK-02
  `evidence/recovery/bk02-restore-recovery-receipt.md` own the backup and
  restore lanes; this packet's two tests are focused interaction tests,
  not recovery-lane drills).

Tests: `full_backup_and_restore_reproduce_archived_state_and_expired_deletions`
(a full backup after the pass restores the live and archived documents,
not the deleted expired one; `deletes == 0` and `documents == 2` bound in
the manifest, because a full backup archives state, not history) and
`an_incremental_chain_replays_the_pass_deletions_on_restore` (base before
the pass, incremental after; the restored chain applies the deletions
exactly).

## V1 citations (pin fa99b925)

| V1 behavior | Citation |
| --- | --- |
| `@expires` / `@refresh` / `@archive-at` / `@archived` metadata names | src/TerraBase.Client/Constants.cs:514, 516, 518, 520 |
| Elapsed `@expires` deletes the document (re-verified in the deleting command) | src/TerraBase.Server/Documents/Expiration/ExpirationStorage.cs:22-42 (delete at 34; `HasPassed` re-check at 31) |
| Expiry/refresh cadence defaults (60 s), batch size (4096), one `currentTime` per run | src/TerraBase.Server/Documents/Expiration/ExpiredDocumentsCleaner.cs:21-27, 133 |
| Due `@refresh` removes the marker and re-puts; `@expires` untouched | src/TerraBase.Server/Documents/Refresh/RefreshStorage.cs:24-57 |
| Elapsed `@archive-at` marks `@archived`, removes the trigger, re-puts | src/TerraBase.Server/Documents/DataArchival/DataArchivalStorage.cs:22-47 |
| Archivist cadence default (60 s) | src/TerraBase.Server/Documents/DataArchival/DataArchivist.cs:32 |
| Missing document drops its background-work entry | src/TerraBase.Server/Documents/BackgroundWork/DocumentBackgroundWorkStorage.cs:13-24 |
| `HasPassed` = `currentTime >= date` | src/TerraBase.Server/Documents/BackgroundWork/AbstractBackgroundWorkStorageBase.cs:30-44 |
| Put indexes `@expires`/`@refresh`/`@archive-at` into the work trees | src/TerraBase.Server/Documents/DocumentPutAction.cs:296-306 |
| A put of an archived document keeps the archived flag | src/TerraBase.Server/Documents/DocumentPutAction.cs:187-193 |
| Archived documents filtered by index/subscription behavior, not by load | src/TerraBase.Server/Documents/Indexes/IndexItem.cs:57-70 |
| Read before expiry returns the document; after expiry+cleaner it is gone | test/TerraBase.SlowTests/Server/Documents/Expiration/ExpirationTests.cs:49-124 |

Named-feature honesty: the brief's V1 pointer list mentions `ExpiresIn` /
`ExpirationRefresh` greps. `ExpiresIn` exists at the pin only as an
unrelated Google Cloud token field
(src/TerraBase.Server/Documents/PeriodicBackup/GoogleCloud/TerraBaseGoogleCloudClient.cs:659)
and in Studio's AI-chat persistence settings; there is no server-side
`ExpirationRefresh` feature by that name at the pin — the feature is the
`@refresh` marker described above.

## Red-then-green evidence

- Red (commit `cfb479d`): the full suite ran against a naive baseline
  (an empty pass and a visibility read that answered `Missing` for
  everything): `cargo test --locked --lib families::lifecycle` → FAILED,
  1 passed (construction validation) / 9 failed, each on its
  discriminating assertion (empty plan lists; no deletion; no refresh
  consumption; no `Expired` state; no archival action; no stale cleanup;
  no tenant scoping observable; both backup-interaction tests failing at
  their first pass-dependent assertion).
- Green (commit `f8fdcb0`): the identical command → 10 passed / 0 failed.
- Repair loop 1 red (unfixed code, pre-`657786c`): the identical command →
  FAILED, 10 passed / 3 failed, exit 101 — each new regression test on its
  discriminating assertion: the duplicate-id tenant's plan "did not
  terminate within 30 s" (the warden's busy cycle, bounded by the test's
  own guard), the squatter-value plan returned `Ok` with
  `stale_records: [doc-1]` (the warden's probe outcome), and the
  stale-owner list read `[doc-1, doc-2, doc-1, doc-2]` against the
  expected `[doc-1, doc-2]`.
- Repair loop 1 green (commit `657786c`): the identical command →
  13 passed / 0 failed, exit 0 (suite wall time 1.13 s — the duplicate-id
  plan that previously never returned now completes inside the suite).

## Repair loop 1 (warden FAIL: findings → fixes → tests → receipts)

Gate outcome: warden FAIL on one MAJOR and two actionable MINORs;
inspector GO with one citation-anchor MINOR. All four are closed below,
red first. The two warden NOTEs that need no code change are recorded
under Limits and Receipts.

### Findings → fixes

1. **MAJOR — `plan_lifecycle_pass` busy-hung forever on a tenant reusing
   one document id across collections.** Root cause: the plan paged the
   tenant through `Engine::snapshot_documents_from`, whose cursor is a
   bare document identifier that resumes strictly after the *first*
   canonical key bearing that identifier (`src/storage/root.rs`,
   `tenant_documents_from`, the shape its own doc warns "can also cycle").
   A window ending inside a reused-id run re-served the same tail window
   forever — two ordinary puts wedged the pass; the original record's
   "bounded snapshot window" phrasing overstated the guarantee and the
   Limits section did not disclose the hang. **Fix (commit `657786c`):**
   the plan now enumerates family records through the landed
   `FamilyStore::snapshot_family_records` materialization — the W3-F seam
   the warden's finding itself named ("as W3-F does"), the same seam
   backup consumes. No cursor exists to cycle; every reserved record is
   observed exactly once; the fail-closed contract for unparseable
   reserved identifiers is byte-identical (both paths reject through
   `kind_of_record` → `ReservedStateInvalid`). Why not the warden's other
   directions: a consumer-side bound (page budget, stall detection) can
   only convert the hang into a typed failure for such tenants — the
   id-only resume *starves* as well as cycles (a reused-id run at least
   one window long keeps the window from ever reaching later keys, so no
   consumer-side cursor trick can advance past it), which is exactly why
   the two landed consumers of the same seam accept bounded loss instead
   (streaming bounds batch window reads; migrations fails a stalled
   composite cursor closed with `MigrationError::PageProgressStalled`).
   A key-cursor or collection-scoped selector at the storage seam is the
   exact paged path — it is recorded as a deferred item, not made here.
2. **MINOR — the record overstated the fail-closed guarantee for
   malformed lifecycle records.** A non-object value (the warden's probe:
   a string) at `family:lifecycle/<doc>` decoded through the shared
   `LifecycleRecord::decode` as the *empty* record — absent fields, no
   error — so the pass returned `Ok` and treated the document as having
   no lifecycle state; only object-with-malformed-field values failed
   closed. **Fix:** a packet-local `decode_lifecycle_record` used at all
   four decode sites (plan, visibility read, both stage paths) rejects
   non-object values with `ReservedStateInvalid`, matching the contract
   `ArchivalRecord::decode` already gave through its required `archived`
   boolean. The record's blanket claim is now true as written; objects
   with absent or `null` fields still decode as absent state.
3. **MINOR — `push_unique`'s "adjacent duplicates only" invariant was
   false.** Canonical key order places every `family:archival/*` record
   before every `family:lifecycle/*` record, so one gone owner with both
   record kinds reached `stale_records` (and through it `cleared_stale`)
   twice, non-adjacently — the warden's `[A, B, A, B]` counterexample.
   No state corruption (the second stage pass no-ops), but the documented
   invariant was wrong. **Fix:** full membership check; every plan list is
   now a set of owners in first-visit order.
4. **Inspector MINOR — one V1 citation anchor was imprecise.** The
   archivist cadence default is `DataArchivist.cs:32`
   (`_archivePeriod = TimeSpan.FromSeconds(... ?? 60)`); line 28 is the
   private constructor signature. Both anchors corrected (D3 and the
   citation table); the D1 evidence bullet's range tightened from
   `:23-28` to `:24-33` so the cited region actually contains the default
   it is cited for. Verified at the pin before editing.

Recorded alongside (inspector NOTE, no action required): the V1 archival
metadata contrast is now explicit at action 4 above — V1 removes
`@archive-at` when it sets `@archived` (DataArchivalStorage.cs:40-42);
V2 keeps `at` as the archival time because the landed
`ArchivalCommand::Archive { at }` vocabulary freezes the schedule in the
record.

### Tests (all red on the unfixed code, green after)

- `the_plan_terminates_and_covers_a_tenant_that_reuses_one_id_across_collections`
  — the warden's exact shape (`doc-x` in `users` and `zzz`, an elapsed
  expiry on `doc-x`). The plan call runs inside a 30 s termination guard
  (`must_terminate`, a worker thread + `recv_timeout`): red = the guard
  fires, because the unfixed plan cycles forever; green = the plan
  completes and still plans the reused id's deletion. The guard bounds
  the regression forever — a reintroduced cycle fails the suite in 30 s
  instead of hanging it.
- `a_non_object_lifecycle_record_value_fails_the_pass_closed` — a squatter
  string document at the reserved `family:lifecycle/doc-1` identifier;
  red = the plan returned `Ok` with the record read as absent state
  (asserted `expect_err`, so the red run prints the exact `Ok` value the
  warden probed); green = `ReservedStateInvalid`.
- `stale_owner_lists_stay_unique_when_owners_have_both_record_kinds` — two
  gone owners, each with a stale lifecycle *and* a stale archival record;
  red = `stale_records` read `[doc-1, doc-2, doc-1, doc-2]`; green =
  unique lists, both record kinds cleared for both owners after the
  staged pass commits.

### Receipts (repair loop, standard set re-run)

See "Receipts" below for the full refreshed set; the repair-specific
runs are the red/green rows in "Red-then-green evidence" above.

## Receipts

Run from the worktree root with `RUSTUP_TOOLCHAIN` unset (the ambient
`stable` override must not replace the repo's 1.97.1 pin):

- `cargo test --locked --lib families::lifecycle` — red: exit 1, FAILED
  (1 passed / 9 failed) at `cfb479d`; green: exit 0, 10 passed / 0 failed
  at `f8fdcb0` and unchanged after the clippy reshaping at `2e842f9`.
- `cargo test --locked --test families` — exit 0, 23 passed (21
  pre-existing + 2 new), red and green states alike.
- `cargo fmt --all -- --check` — exit 0 (one `cargo fmt --all`
  application was needed first; the check failed with diffs before it).
- `cargo check --workspace --locked` — exit 0.
- `cargo test --workspace --locked` — exit 0; 45 test-target summaries
  all `ok`, 1215 tests passed, 0 failed (run in the background: the
  foreground run exceeded the ten-minute tool timeout under this host's
  concurrent fleet builds — a single timing-bound timeout, not a test
  failure; the completed background run is the recorded evidence).
- `cargo clippy --workspace --all-targets --all-features --locked --
  -D warnings` — exit 0, after fixing 8 findings in this packet's files
  (an unneeded `Ok(..?)`, a missing-panics-doc from a test-only
  `expect`, two boolean simplifications, a too-long function reshaped
  into per-action helpers, two by-value context arguments under the
  seam-contract allow, and two redundant closures).
- `TERRABASE_V1_ROOT=<pinned clone> ./scripts/check.sh` — first run:
  every stage through the secret scan passed, then the V1 citation audit
  stage exited 1 with "unable to resolve the TerraScale workspace root"
  — this packet's worktree is two levels below the repository root, so
  the script's candidate walk cannot find the workspace. Re-run with
  `TERRASCALE_WORKSPACE_ROOT` also set: exit 0, "TerraBaseV2 foundation
  checks passed", including the strict-mode V1 citation audit and the
  secret scan; `cargo-deny` is unavailable on this host and is recorded
  by the script itself as unrun.

### Repair loop 1 refreshed receipts

All from this worktree at the repair commits, `RUSTUP_TOOLCHAIN` unset:

- `cargo fmt --all -- --check` — exit 0.
- `cargo check --workspace --locked` — exit 0.
- `cargo test --locked --lib families::lifecycle` — red (unfixed code):
  exit 101, FAILED, 10 passed / 3 failed, each failure on its
  discriminating assertion (termination guard fired at 30 s; squatter
  plan returned `Ok`; stale list `[doc-1, doc-2, doc-1, doc-2]`). Green
  (at `657786c`): exit 0, 13 passed / 0 failed.
- `cargo test --locked --test families` — exit 0, 23 passed / 0 failed.
- `cargo test --workspace --locked` (dev profile, the required receipt) —
  exit 0; 45 test-target summaries all `ok`, 1218 passed, 0 failed — the
  original loop's 1215 plus this loop's three regression tests. Run in
  the background: the host is carrying concurrent fleet builds.
- `cargo clippy --workspace --all-targets --all-features --locked --
  -D warnings` — exit 101 first (one `match_wild_err_arm` on the new
  termination guard's `Err(_)` arm), exit 0 after splitting the
  recv-timeout error arms.
- `python3 scripts/validate_v1_citations.py --mode strict --v1-root
  <pinned fa99b925 clone> --workspace-root /home/mariogk/Projects/TerraScale
  --gap-report parity/v1-test-gaps.json` — exit 0, "STRICT V1
  QUALIFICATION PASSED". Run standalone because check.sh did not reach
  its citation stage in either run below; the two anchors this loop
  corrected are covered by this pass.
- `TERRABASE_V1_ROOT=<pinned clone>
  TERRASCALE_WORKSPACE_ROOT=/home/mariogk/Projects/TerraScale
  ./scripts/check.sh` — run 1: exit 101; the protocol re-run: exit 101.
  Both runs aborted in the RELEASE test stage at the pre-existing SE-15
  wall-clock test `benchmarks::tests::a_self_comparison_reads_as_parity`
  (src/benchmarks/tests.rs:492, landed before this packet's base and
  untouched by it) — release lib 820 passed / 1 failed / 1 ignored both
  times, while the same test passed in each run's DEV stage (821 passed /
  0 failed) and standalone under release (`cargo test --locked --release
  --lib benchmarks::tests::a_self_comparison_reads_as_parity -- --exact`
  → exit 0 in 0.29 s). This is the host-load sensitivity the warden's
  note pre-attributed (their own rerun failed identically and passed
  standalone); both runs here executed under several concurrent fleet
  builds. Every stage check.sh completed before that abort was green in
  both runs; the stages after it — the citation audit (covered standalone
  above) and the cargo-deny stage (still unavailable on this host,
  recorded by the script as unrun) — did not execute inside check.sh.
  Recorded as **failed twice at a pre-existing load-sensitive test
  outside this packet's write scope**, not as green.

## Deferred items (named owners)

- **`Engine::maintain_lifecycle` wrapper** (D1's proposed collision
  action): parent ratifies or drops; one-line delegating method in
  `src/engine/mod.rs` plus a host-side timer policy. Owner: parent +
  engine owner.
- **Engine-seam expiry filtering** (D5): making `Engine::read` /
  `Transaction::read` hide expired documents engine-wide requires the
  engine to consult family state on read — an engine-contract decision.
  Owner: engine owner, after parent ratification of the desired read
  model. Until then, callers use `read_document_visibility`.
- **Archived-document visibility in query and subscription behavior**
  (V1 `ArchivedDataProcessingBehavior`, IndexItem.cs:61-70): the query and
  streaming seams do not consult archival state today. Owner: query wave
  (Q*) and streaming (SE*) owners.
- **Per-pass document-count bound** (V1's `MaxItemsToProcess` /
  `DeleteFrequencyInSec` configuration, ExpiredDocumentsCleaner.cs:21-29):
  a host can bound work by committing per plan slice; a first-class knob
  (`LifecyclePassRequest` with a bound) is a payloads collision if wanted.
  Owner: parent.
- **Key-cursor or collection-scoped enumeration at the storage seam**
  (repair loop 1): `snapshot_documents_from`'s identifier-only cursor
  cycles and starves on tenants that reuse identifiers across collections
  (`src/storage/root.rs` names "collection scoped selectors" as the exact
  path; `src/storage/tests.rs` models the cycle), which is why the plan
  materializes through `snapshot_family_records` today. A
  `(collection, id)` cursor would restore paged planning without the
  materialization memory profile. Owner: storage owner, with the
  migrations and streaming consumers (both already bound or fail closed
  against the same shape).
- **Cadence configuration** (V1's `ExpirationConfiguration` /
  `RefreshConfiguration` / `DataArchivalConfiguration`): no V2
  configuration surface exists; the host owns cadence per D1. Owner:
  host/Platform adapter owner.
- **Refresh-execution observability**: V1's refresh re-put changed the
  document's change vector and could wake subscriptions
  (SubscriptionConnection.cs:378); V2's consumption rewrites only the
  family record, so a subscription on the user document sees nothing. A
  change-notification mapping, if wanted, belongs to the SE wave's
  backbone consumers. Owner: SE owner.
- **Archived documents still expire** (V1 parity: ExpirationStorage does
  not check the archived flag): landed as-is; if V2 wants archived
  documents exempt from expiry, that is a ratifiable semantic change.
  Owner: parent.

## Proposed collision actions

None **required**. Recorded proposals (not made):

1. `Engine::maintain_lifecycle(&self)` — one-line delegating method in
   `src/engine/mod.rs` mirroring `maintain_indexes`, if the parent prefers
   the engine-method shape (D1).
2. `ArchivalCommand::Schedule { at }` — a public payloads vocabulary for
   schedule materialization (D3), replacing the pass's internal record
   write; lives in `src/families/payloads.rs` + `state.rs`.
3. Root re-export of the new lifecycle execution vocabulary
   (`LifecyclePass`, `LifecyclePassStaged`, `VisibilityRead`,
   `VisibilityReport`, `VisibilityState`, `DocumentVisibility`) in
   `src/lib.rs`, if callers should reach them without the
   `families::lifecycle` path. They are public at
   `terrabase_v2::families::lifecycle::*` today.

One shared-file edit was made inside the packet's declared scope:
`tests/families/main.rs` gained the single `mod lifecycle_execution_public;`
wiring line for the new integration file (the wiring pattern every prior
tests/families packet used).

## Limits and disclosed behaviors

- A pass is not crash-safe across its plan and stage by itself: the plan
  is advisory, every action re-verifies in the staging transaction, and a
  crash before the driver's commit stages nothing (a rejected or dropped
  transaction leaves no partial state). A commit that reports `Unknown`
  keeps the operation identity for durable resolution like any
  transaction.
- A writer that lands between the staging transaction's snapshot and its
  commit rejects the whole pass commit through the
  `ExpectedVersion::Exact` delete (the engine's commit-time conflict
  check, src/storage/file.rs:349-357). The host retries the pass; skipped
  actions are per-pass facts, not durable state.
- The plan materializes the tenant's documents once per pass through
  `snapshot_family_records` (repair loop 1's replacement for windowed
  paging) and does not bound total actions: a tenant with millions of
  expired documents holds one tenant-sized snapshot read and stages one
  large transaction unless the driver slices. The materialization memory
  profile is the price of the termination guarantee below; a collection-
  scoped or key-cursor enumeration seam would restore paged planning and
  is noted under deferred items.
- A malformed value at a reserved record identifier — since repair loop 1
  including a *non-object* lifecycle value, which the shared decoder alone
  read as absent state — or a reserved-collection identifier that does not
  parse as a family record, fails the pass closed with
  `ReservedStateInvalid` (the recorded W3-F collision limit — the engine
  does not reserve the collection name), so a squatter document can block
  maintenance until cleared. An object whose fields are absent or `null`
  still decodes as absent state, mirroring `snapshot_family_records` and
  `ArchivalRecord::decode`'s existing fail-closed shape.
- Deleting an expired document does not cascade to its other family
  records (the W3-F no-cascade limit stands): the pass's deletion clears
  only the lifecycle record and records only the lifecycle tombstone;
  counters, attachments, and revisions of a deleted document survive until
  their own owners act. The pass's separate **stale** action does clear
  both the lifecycle and the archival record of an owner that is gone —
  including a marked/archived one — because a record whose subject is gone
  is stale state (V1's queue cleanup has the same shape,
  DocumentBackgroundWorkStorage.cs:13-24); a restore can re-materialize
  such records from an archive taken before the cleanup.
- The visibility batch read validates its deadline and cancellation once
  at entry and then performs one point read per requested document without
  mid-loop signal rechecks; the document list is caller-bounded, matching
  the single-document family reads (warden note, accepted as-is — a
  mid-loop check becomes owed only if this read grows a server-side
  bound).
- `LifecyclePass` and the staged receipt carry document identities only —
  no document values, content, paths, or credentials.
- Times are epoch milliseconds from `SystemTime`
  (`now_millis`); no time qualification is claimed, and no clock
  synchronization across members is claimed.

## What is not claimed

- No V1 parity claim: PB-3033 and the data-archival rows remain
  unresolved; this record adds no Proof ID. The red/green runs are
  focused tests, not parity qualification.
- No recovery, durability, replication, multi-region, convergence, or
  performance claim; no Recovery, Replication, or Benchmark Receipt. The
  two backup-interaction tests are focused seam tests, not recovery-lane
  drills (BK-02 owns that lane).
- No query, index, subscription, or streaming claim: nothing under
  `src/query/**`, `src/index/**`, or `src/streaming/**` was touched or
  consulted; expired-document filtering in those seams does not exist.
- No host implementation: no timer, scheduler, or cadence configuration
  shipped; D1's driver-side contract is documented, not implemented.
- No change to `parity/**`, `src/lib.rs`, `src/types.rs`,
  `src/operations.rs`, `src/families/{mod,payloads,state,store,tests}.rs`,
  `src/engine/**`, `src/backup/**`, or CI files.
