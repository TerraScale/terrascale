# BK-03 focused-run receipt: smuggler export/import and logical migration

Focused tests only. No recovery-lane claim: crash/kill-point restore
drills, Recovery Receipts, and durability qualification belong to the
exclusive recovery lane (BK-02). Interruption behavior here is proved by
deterministic per-record cancellation, not by process kills. No secret,
credential, customer data, export or backup content, or private host path
appears in this record.

- Ticket: BK-03 (issue #44)
- Branch: `fleet/bk03-2`, base `develop` @ `c6359a9`
- Module family: `src/backup/smuggler/**`, `src/migration/**`,
  `tests/recovery/**`
- Inputs consumed: the accepted WS-BACKUP design contract (`93f04ee`, as
  integrated in `docs/design/w4-transfer-replication-contract.md`), BK-01's
  landed backup core, and the families seam's public write payloads.
- Dependency recorded: GAP-D02 (#59, V1 change-vector / metadata mapping)
  had **not landed** when this packet ran. It is consumed as an input once
  it lands; until then the migration records the dependency in its module
  contract (compare-exchange converts through the missing-key condition; no
  V1 change vector or raft index is preserved) instead of silently
  dropping it.

## Commands run (pinned offline flags)

From the worktree root, toolchain from `rust-toolchain.toml`,
`Cargo.lock` unchanged:

- `cargo fmt --all -- --check` — pass
- `cargo check --offline --locked` — pass, no warnings
- `cargo clippy --offline --locked --all-targets --all-features -- -D warnings` — pass
- `cargo test --offline --locked` — pass (362 lib tests plus every
  integration target, 0 failures; includes the 33 new tests below)
- `bash scripts/check.sh` with
  `TERRABASE_V1_ROOT=/home/mariogk/Projects/TerraScale/TerraBase` and
  `TERRASCALE_WORKSPACE_ROOT=/home/mariogk/Projects/TerraScale` — see the
  stage table below.

## Focused behavior proved

### Smuggler export (crate-internal seam, `src/backup/smuggler/`)

- `export_writes_a_manifest_with_family_selection_counts_and_digests` —
  per-family counts (Documents 3, FamilyRecords 1, Tombstones 1 on the
  fixture), a 64-hex content-derived identity, and member digest
  verification.
- `export_selection_excludes_unselected_families` — unselected families
  never enter the artifact.

### Smuggler import (through the Engine transaction seam)

- `round_trip_export_import_preserves_families_with_checksums` — export
  from engine A, per-tenant scoped import into engine B, re-export from B:
  identical identity and identical member digests and lengths for the
  Documents and FamilyRecords families.
- `import_applies_collection_rename_transforms` — typed rename
  `Orders -> orders-v2` applied to 2 records; family records untouched.
- `import_rejects_a_transform_targeting_the_reserved_collection` —
  `TransformInvalid` before any write.
- `import_is_idempotent_on_reimport` — second run applies 0 records,
  resolves every first-run record as a duplicate, final state identical
  (3 documents, exactly once each).
- `interrupted_import_resumes_idempotently` — cancellation after 1 applied
  record, resumed run resolves the durable prefix as duplicates and
  completes; final state equals a single uninterrupted import.
- `import_rejects_a_tampered_manifest_before_any_write` — identity-bound
  count edit is `ManifestInvalid`; target left empty.
- `import_rejects_a_digest_mismatch_before_any_write` — same-length member
  byte edit is `ChecksumMismatch`; target left empty.
- `import_rejects_a_truncated_or_corrupt_record` — truncated member tail
  is `RecordTruncated`, unknown record tag is `RecordCorrupt` (both with
  the manifest rebound over the damaged bytes, the corrupt-producer
  shape).
- `import_rejects_an_unsupported_manifest_version` —
  `UnsupportedVersion`.
- `import_rejects_a_selection_the_artifact_does_not_carry` — requesting
  Tombstones from an artifact without them is `ManifestInvalid`.
- `import_isolates_tenants_from_the_supplied_scope` — a beta-scoped import
  applies only beta's records; alpha's data never entered.
- `import_applies_tombstones_as_engine_deletes` — imported tombstone keeps
  the deleted document absent in the target.

### Logical Migration (public seam, `src/migration/`)

In-crate (data-bearing, `src/migration/tests.rs`, 13 tests):

- `migration_validates_the_v1_export_manifest_before_any_write` — a
  tampered count fails identity binding; nothing written.
- `migration_rejects_an_unsupported_manifest_version` — version 2 refused,
  nothing written.
- `migration_writes_only_through_the_engine_transaction_seam` — documents
  read back through the engine read path; the counter reads back as 9 and
  the identity seed as next=41 through the family read seam.
- `migration_is_idempotent_for_the_same_input_and_target_state` — second
  run converts 0, resolves duplicates, same final state.
- `interrupted_migration_resumes_from_its_last_durable_record` —
  cancellation after 2 applied records; resumed run resolves the durable
  prefix and matches a clean run's outcome.
- `migration_reports_an_explicit_loss_matrix_with_reasons_and_counts` —
  all nine contract categories present; every dropped category carries a
  count and a reason; documents convert 3/0.
- `migration_verifies_counts_and_checksums_before_reporting_success` —
  verdicts green; a manifest whose counts lie (identity recomputed over
  the lying body) fails closed.
- `migration_rejects_a_truncated_or_corrupt_input_record` —
  `RecordTruncated` / `RecordCorrupt`, nothing written.
- `migration_rejects_a_tampered_segment_digest` — `ManifestInvalid`,
  nothing written.
- `migration_tombstone_policy_controls_convert_or_drop` — Convert 1/0;
  DropRecorded 0/1 with a reason.
- `migration_revision_policy_latest_only_collapses_to_the_newest_version`
  — AllVersions converts 3 records (engine version 3); LatestOnly converts
  1 (engine version 1) with collapsed revisions disclosed.
- `migration_selection_skips_unselected_kinds_with_recorded_drops` —
  unselected counter/time-series records are dropped with counts and a
  reason, never silent.
- `logical_migration_makes_no_direct_v1_file_open_claim` — a directory
  with a raw V1 database file and no representation manifest is refused.

Public integration (`tests/recovery/migration_public_contract.rs`, 7
tests): valid hand-crafted manifest loads and verifies; unsupported
version; tampered identity; path-traversal member names; unknown lines and
kinds; missing manifest / missing directory; the no-direct-V1-file-open
rule. Module unit tests additionally pin the migration-side SHA-256
against the FIPS empty-string and `abc` vectors and chunked-update
equivalence (3 tests in `src/migration/digest.rs`).

## The V1-representation drill (synthetic, honestly labeled)

The end-to-end migration drill runs against a **synthetic**
V1-representation fixture produced by the crate's test-only fixture
builder (`src/migration/fixture.rs`) — every loss-matrix record kind,
multi-segment artifact, manifest identity recomputed independently inside
tests, per-record cancellation for interruption. No live V1 store was
opened: the pinned V1 checkout (`fa99b9257776dbe625e6a2388c74c9ce916637ee`)
is the V1-semantics reference the representation format documents against,
and running a genuine V1 export is an open limit recorded here, not a
claim. GAP-D02's mapping is the other open input.

## `scripts/check.sh` stages

| Stage | Outcome |
| --- | --- |
| `cargo fmt --all -- --check` | pass |
| `cargo check --workspace --locked` | pass |
| `cargo test --workspace --locked --release` | pass |
| `cargo clippy --workspace --all-targets --all-features -- -D warnings` | pass |
| `cargo doc --workspace --no-deps --locked` | pass, one warning — the pre-existing `StreamStore` link warning in `src/engine/mod.rs` (outside this packet's scope, present before it) |
| parity inventory (258 items, schema 1.0, source `fa99b925`), parity tooling, V1-citation unit suite, license check, secret scan | pass |
| V1 citation audit (strict) | fails before examining any citation: the shared pinned V1 checkout (`fa99b925`) carries local modifications, the identical environmental failure BK-01 recorded at `develop` @ `431841a`; unrun-in-effect, not caused by this packet |
| `cargo deny check` | not reached (the script aborts at the audit stage before it); `cargo-deny` availability unverified, recorded as unrun |

## Collision actions taken

- `tests/recovery/main.rs` — one `mod migration_public_contract;` line
  added to declare this packet's new integration test file (the shared
  harness file's only in-scope change).
- `src/backup/mod.rs`, `src/lib.rs`, `Cargo.*` — untouched; the smuggler
  seam stays crate-private (the declaration-only root declared
  `mod smuggler;` privately), so exposing it to the backend host is an
  integrator action under PD-2026-08-26-11 D2.

## Limits

- Focused tests only; the exclusive recovery lane (BK-02) owns kill-point
  and fault drills and formal Recovery Receipts.
- Migration decodes one segment into memory at a time (streaming decoder
  is an open improvement); smuggler export replays one tenant's live state
  in memory, inheriting BK-01's documented state-archive memory bound.
- Counter records apply as increments of the V1 value — exact on a fresh
  target; duplicate prevention on resume is by operation identity and the
  engine ledger's identical-write-set replay, not by subtraction.
- Smuggler round-trip identity equality is proved for the
  Documents+FamilyRecords selection of single-version state: tombstone
  records and re-versioned documents preserve logical content but not
  byte identity (a delete record carries its source journal's version
  facts; V2 assigns its own document versions).
- The representation format is V2-documented and synthetic; no live V1
  export has been run against it.

## Warden repair loop 1

Three warden findings on packet `aba8ecc`, fixed exactly as scoped; every
fix carries a red-then-green regression test run against the unfixed tree
first.

### [MAJOR 1] `clippy::duration_suboptimal_units` under the pinned toolchain

`cargo +1.97.1 clippy --offline --locked --all-targets -- -D warnings`
failed on two packet sites (`src/migration/mod.rs` `from_secs(3600)`,
`src/migration/tests.rs` `from_secs(60)`). Fixed to
`Duration::from_hours(1)` / `Duration::from_mins(1)` per clippy's
suggestion; the pinned clippy now exits 0 and the full `scripts/check.sh`
runs through to exit 0 (below).

### [MAJOR 2] operation identities unstable across selections/policies

Both runtimes advanced the record ordinal only after the family-selection
filter (smuggler `ImportRun::record`) or after selection/policy routing
(migration `Run::record`/`Run::apply`), so the same record carried
different ordinals — and different records collided on one ordinal —
across runs with different selections or policies. An interrupted partial
import under selection A could leave ordinal N durable, and a resumed
import under selection B then resolved a *different*, never-applied record
as that duplicate and silently skipped it while reporting success (both
per-tenant and per-kind count verifications are satisfied by the
misattributed duplicate).

Fix: the ordinal advances for every decoded record, in decode order,
before any selection/policy routing in both runtimes, binding identity to
(artifact identity, record decode position), option-independent. The
`LatestOnly` deferral keeps its original ordinal via the `document`/
`apply_at` parameter instead of ordinal save-and-restore. Regression
tests, both red before the fix (`migration_operation_identities_are_
stable_across_selections`, `import_operation_identities_are_stable_across_
selections`): a partial run under selection A (documents only /
family records only) is cancelled after one durable record; the resumed
run under selection B (all kinds / all families) must convert the skipped
record, resolve exactly the partial run's record as its duplicate, and
account every decoded record exactly once — without the fix the
migration test fails at `converted(MigrationKind::Identity) == 0` (the
identity record counted as a duplicate it never applied) and the smuggler
test fails at `applied(Documents) == 1` with `duplicates(Documents) == 1`
(order-1 silently skipped, absent from the target).

### [MAJOR 3] documented document-order contract not enforced while decoding

`src/migration/codec.rs` documents ascending `(tenant, collection, id)`
with ascending versions of one key adjacent and "enforced while decoding",
but `decode_segment` carried no ordering state (the check existed only in
the test fixture producer). A descending-key stream could silently convert
a non-newest version as a key's latest under `LatestOnly`.

Fix: `DocumentOrder` tracks the previous document key and version;
`decode_segment` observes it per frame and returns
`MigrationError::RecordCorrupt` on any key that sorts below its
predecessor or any non-ascending version of the same key. The order
contract spans the concatenated record stream, so `run_migration` threads
one state through every segment in manifest order. Red before the fix:
`migration_rejects_a_document_order_violation_while_decoding` feeds a
hand-encoded descending-key segment (and a descending-version variant)
through the production `run_migration` path with the manifest rebound over
the bytes — the corrupt-producer shape — and asserts `RecordCorrupt` with
nothing written; without the fix the run reported success (`err() ==
None`).

### Commands re-run after the fixes

- `cargo fmt` — applied (minor line wraps in the new tests); the final
  tree is fmt-clean, which `check.sh`'s `--check` stage re-proves
- `cargo +1.97.1 clippy --offline --locked --all-targets -- -D warnings`
  — pass, exit 0 (the two MAJOR 1 sites cleared)
- `cargo check --offline --locked` — pass, exit 0, no warnings
- `cargo test --offline --locked` — pass: 365 lib tests (362 before the
  repair plus the 3 new regression tests) and every integration target,
  0 failures
- `bash scripts/check.sh` with
  `TERRABASE_V1_ROOT=/home/mariogk/Projects/TerraScale/.worktrees/terrabase-v1-fa99b925-readonly`
  and `TERRASCALE_WORKSPACE_ROOT=/home/mariogk/Projects/TerraScale` —
  exit 0 (the V1 citation audit now runs against the pristine read-only
  pinned checkout; the packet-stage environmental failure recorded above
  did not recur)
