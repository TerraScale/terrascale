# GAP-F05 — Revisions completeness (TerraBaseV2 #74)

Packet: GAP-F05, workstream GAP-F05, ticket TerraBaseV2#74 (folds D17 per
charter PD-2026-08-26-14 D3). Base commit `da0df43`. Branch
`fleet/gapf05-2`, worktree `.worktrees/terrabasev2-gapf05`. Owner: GAP-F05
builder; parent ratification required for the PROPOSED decisions below.

V1 source identity for every citation in this record: pinned commit
`fa99b9257776dbe625e6a2388c74c9ce916637ee`
(`.worktrees/terrabase-v1-fa99b925-readonly`), paths under
`src/TerraBase.Server/Documents/` and
`src/TerraBase.Client/Documents/Operations/Revisions/`.

## What landed

`src/families/revisions.rs` — the GAP-F05 completeness layer of the
[`Revisions`](../src/families/mod.rs) family, as typed operations on
`FamilyStore` (the GAP-F01 house pattern):

- **Collection revision policies.** `CollectionRevisionsConfiguration`
  (minimum revisions to keep, minimum age to keep, maximum deletions per
  document update, purge-on-delete, disabled) and
  `RevisionsDatabaseConfiguration` (a default policy plus per-collection
  policies plus the bin minimum age), staged whole-record through
  `stage_revisions_configuration` and read through
  `read_revisions_configuration`. Resolution is V1's: the collection's own
  entry, else the default, else disabled.
- **Policy enforcement.** Every `stage_revision_snapshot` applies the
  effective policy to the document's whole revision log — V1's
  `DeleteOldRevisions` on every document update — and
  `stage_enforce_revision_policy` applies it on demand, including
  purge-on-delete of a deleted document's whole log.
- **Revision bin and cleaner.** `read_revisions_bin` pages the deleted
  documents whose revision logs passed the age cutoff;
  `stage_revisions_bin_clean` purges the listed entries with re-checked
  eligibility and all-or-nothing staging.
- **Revert-by-change-vector.** `stage_revert_to_contract` restores the
  revision addressed by GAP-D02's `DocumentVersionContract`
  (`src/engine/metadata.rs`, landed as #59), rewriting the user document
  and re-putting the snapshot's attachment references by digest.
- **Attachment snapshots.** Each snapshot captures the document's
  attachment references — name, content type, size, GAP-F01 digest — and
  `read_revision_snapshots` exposes value, contract, revert provenance,
  and references per revision (PB-3005's historical reads). References
  only; bytes stay in the content plane.

Storage: the W3-F revision log (`family:revisions/<document>`) stays the
canonical value log and every existing `RevisionCommand` path is
untouched. GAP-F05 facts live in two new reserved records under distinct
kind keys (see collision actions): the tenant configuration
(`family:revision-configuration/root`) and the per-document snapshot side
car (`family:revision-snapshots/<document>`), which holds the collection,
per-revision version contract, revert provenance, and attachment
references, kept in lockstep with the log (trimmed entries are reaped on
every GAP-F05 write — the repair-loop 1 fix below made this hold when the
W3-F per-document policy, not the collection policy, is the trimming
agent; reads hide side-car entries whose revision left the log).

Tests: 22 focused inline tests (`#[cfg(test)] mod tests` in the module;
18 at handoff plus 4 repair-loop regression tests) and 4
public-construction integration tests (`tests/families/revisions_public.rs`).
Red-then-green: the full suite was written first and compiled-failed on
exactly the missing API (receipt below), then the implementation turned it
green; the repair-loop regression tests landed red first against the
handed-off tree (receipt below).

## Decision record D1 (PROPOSED — parent ratification required): ownership of revision state, policies, and enforcement

**Question.** Where do revisions live in V2: in the document contract (the
engine's per-document metadata, auto-snapshot on every write, V1's
storage-table model) or in the `Revisions` family kind (typed seam,
reserved records)?

**Decision.** State and policies live in the family kind; the document
contract supplies identity only; enforcement splits by mechanism:

1. **Revision state** — the per-document revision log — is family state in
   `family:revisions/<document>`, per the W3-F D4 contract alignment
   (charter ledger 2026-08-24, `FamilyKind::Revisions`; GOAL.md D4 record).
   GAP-D02's `DocumentVersionContract` is the *change identity* each
   snapshot records, not a storage location for history.
2. **Revision policies** — collection-scoped enable/default configuration —
   are family state under `family:revision-configuration/`, mirroring V1's
   database-record `RevisionsConfiguration`
   (`RevisionsConfiguration.cs:23-32`: `Default` plus a per-collection
   dictionary; server resolution `RevisionsStorage.cs:176-194`; the empty
   disabled configuration `RevisionsStorage.cs:62` returned at `:193`).
3. **Enforcement** splits:
   - *Policy application and bin cleaning* are typed, caller-invoked
     operations in this module (`stage_revision_snapshot`,
     `stage_enforce_revision_policy`, `read_revisions_bin`,
     `stage_revisions_bin_clean`). The family seam owns no background
     worker: V1's `RevisionsBinCleaner` is a server timer
     (`RevisionsBinCleaner.cs:16`, `DoWork` at `:30-35`) whose cadence
     (`CleanerFrequencyInSec`, default 5 minutes) is an operational
     schedule the host owns, not family behavior. The bin age itself is
     configuration (30-day default, `RevisionsBinConfiguration.cs:27`) and
     rides the family's configuration record.
   - *Write-time snapshot creation* (V1's `ShouldVersionDocument` on every
     document put, `RevisionsStorage.cs:196-278`) belongs to the Engine
     write path through the same crate-private hook pattern SM-02 set for
     schema validation. **This packet does not implement it and does not
     edit `src/engine/**`**; it is proposed as the parent collision action
     CA-1 below. Until it lands, snapshot creation is this module's
     explicit operation.

**Evidence.**

- V1 gates revisions per collection with an explicit disable
  (`RevisionsCollectionConfiguration.Disabled`,
  `RevisionsCollectionConfiguration.cs:12`); unconditional auto-snapshot in
  the document contract cannot express that.
- V1's per-collection resolution order and the disabled fallback
  (`RevisionsStorage.cs:176-194`) map exactly onto family-held
  configuration with an `effective` resolution.
- V1's delete path separates policy purge (`PurgeOnDelete`,
  `RevisionsStorage.cs:627-631`) from the bin cleaner's age rule
  (`RevisionsBinCleaner.cs:97`) — two different mechanisms that both
  operate on stored revision state, i.e. family operations.
- The charter's family seam doctrine (families never commit independently;
  state in reserved records; `src/families/mod.rs` module contract) and the
  W3-F D4 alignment fixing `FamilyKind::Revisions`.
- GAP-D02 already separates identity (contract) from storage: the contract
  is "a slot for the replication lane's fact, never a laundered V1 etag"
  (`src/engine/metadata.rs:16-21`).

**Rejected alternatives.**

- *R1: revisions inside the document contract* (per-document engine
  metadata auto-snapshotting on write, history in the engine's storage).
  Rejected: contradicts the charter's W3-F D4 family alignment; makes
  retention unconditional (V1's `Disabled` gate could not be expressed);
  couples unbounded storage growth to every write without policy; and
  would move revision policy into `src/engine/**`, which this packet may
  not edit.
- *R2: a family-owned background cleaner thread.* Rejected: the family
  seam is transaction-riding and thread-free by contract (families never
  commit independently). V1's cleaner cadence is a database-record
  operational concern. The host/session layer schedules the typed clean
  operation.
- *R3: keep per-document policies only* (the W3-F status quo:
  `RevisionCommand::Configure`). Rejected as incomplete: V1's semantics
  are collection-scoped with a database default; per-document policy
  cannot express "enabled for collection X with these limits" and leaves
  application to every writer. The per-document policy is untouched and
  composes (both trims apply; the side car follows the log).
- *R4: inline attachment bytes into snapshots.* Rejected: V1 stores
  attachments hash-addressed and re-puts only metadata on revert
  (`AttachmentsStorage.PutAttachmentRevert`, `AttachmentsStorage.cs:611-652`);
  GAP-F01's plane is digest-addressed and deduplicated; inlining would
  multiply stored bytes per revision.

## Decision record D2 (PROPOSED): snapshot identity is the captured publication contract

Each snapshot records the document's current **published**
`DocumentVersionContract` (via GAP-D02's `Transaction::head`). A revert
addresses the revision by that contract, exactly V1's revert-by-change-
vector (`RevertRevisionsByIdOperation.cs:19-33`; server verification
`RevisionsStorage.cs:2623-2643`). A revert-created revision deliberately
carries **no** contract of its own (its publication identity exists only
after commit; recording a guess would launder identity) — it carries revert
provenance (`reverted_to`) instead, so contract addressing stays
unambiguous — with one disclosed nuance: a document snapshotted twice
without change captures the same contract twice, and a revert to that
contract resolves to the first matching revision; the values are
identical (the document never changed), so the restore is equivalent
(warden repair-loop finding, disclosed here rather than silently
assumed). A snapshot staged in the same transaction as a document write
fails closed (`StorageFailure`) when the view's version and the provable
publication contract disagree: the contract is a publication fact, never a
guess (GAP-D02's `resolve_published` discipline).

## Decision record D3 (accepted in-packet): the caller owns the clock

GAP-F05 operations take the caller's logical `now_ms` for age evaluation
and recording times. V1 uses the server clock
(`_database.Time.GetUtcNow()`, e.g. `RevisionsStorage.cs:807`); V2's family
seam owns logical times only, and the host owns the schedule that drives
snapshotting and cleaning. This keeps the policy arithmetic deterministic
and testable and matches the W3-F convention that family records carry
logical times.

## Fail-closed strengthenings over V1 (disclosed differences)

- The typed constructors refuse a zero minimum-keep count, a negative
  minimum age, and a zero per-update deletion cap. V1's client type carries
  any number (`RevisionsCollectionConfiguration.cs` has no validation); a
  zero minimum would mean "keep nothing" and a zero cap "delete nothing",
  so the typed surface refuses them instead of computing a degenerate
  policy.
- Bin eligibility requires the document to be absent. V1's bin is the set
  of deleted documents' revisions identified by delete-revisions
  (`GetDeletedRevisionsIds`); V2's revision records do not store delete
  markers, so absence is the delete marker. A deleted-then-re-created
  document is therefore never a bin entry.

## V1 citations (pinned commit fa99b925)

| Behavior | V1 evidence |
| --- | --- |
| Configuration shape: default + per-collection map | `TerraBase.Client/Documents/Operations/Revisions/RevisionsConfiguration.cs:23-32` |
| Collection policy fields (min count, min age, disabled, purge-on-delete, max deletions per update) | `.../RevisionsCollectionConfiguration.cs:9-32` |
| Bin configuration: disabled switch, 30-day default age, cleaner cadence | `.../RevisionsBinConfiguration.cs:16-36` (`Disabled` `:16`, age `:27`, cadence `:36`) |
| Resolution order: collection, default, empty-disabled fallback | `src/TerraBase.Server/Documents/Revisions/RevisionsStorage.cs:176-194`, empty config `:62` |
| Per-update trim arithmetic, age floor, cap, `HasMore` | `RevisionsStorage.cs:754-835` (both limits unset yields nothing at `:768-772`; age keeps at `:806-818`; cap at `:778-783`; `HasMore` at `:833`) |
| Purge-on-delete of a deleted document's revisions | `RevisionsStorage.cs:627-631` |
| Bin batch bound `10 * 1024` (64-bit) | `RevisionsBinCleaner.cs:27` (V1's conflict-configuration defaults at `RevisionsStorage.cs:77-85` use the same number; conflict configuration itself is GAP-F07's, deferred) |
| Bin cleaner: schedule, `before = now - minAge`, batched id walk, merged command | `src/TerraBase.Server/Documents/RevisionsBinCleaner.cs:16-35`, `:80-149` (age at `:97`, ids at `:108-109`, command at `:118-119`, batch `:27`) |
| Revert-by-change-vector client operation | `.../Revisions/RevertRevisionsByIdOperation.cs:19-33` |
| Revert verification (cv must exist and belong to the document) | `RevisionsStorage.cs:2623-2643` |
| Revert command (restore value, delete-then-re-put attachment metadata) | `RevisionsStorage.cs:2571-2620`, `RemoveOldMetadataInfo`/`InsertNewMetadataInfo` at `:2645-2660` |
| Attachment revert re-puts by name/content-type/hash/size — never bytes | `src/TerraBase.Server/Documents/AttachmentsStorage.cs:611-652` (`DeleteAttachmentBeforeRevert` at `:590`) |
| Force-revision creation command (the explicit path W3-F already mirrors) | `TerraBase.Client/Documents/Commands/Batches/RevisionsCommandData.cs:9-45` |

## Parity rows this packet advances (builders do not flip rows)

- **PB-3025 revisions-crud-range** (remainder): revert-by-change-vector and
  single-revision reads land here. Still open: revision includes,
  collection statistics.
- **PB-3026 revisions-config-bin-retention** (remainder): default and
  per-collection policies, the bin, the bin cleaner, and retention-by-age
  land here. Still open: conflict-revision configuration (GAP-F07).
- **PB-3005 attachments-revisions**: attachment revision snapshots and
  historical reads land here, composing with the landed GAP-F01 plane.

## Deferred items (named owners)

- **Write-time snapshot creation on every document put** (V1
  `ShouldVersionDocument`): proposed engine write-path hook (CA-1). Owner
  after ratification: parent collision action in `src/engine/**` + a
  follow-up in this module; the families module's operation is ready to be
  called from that hook.
- **Conflict-revision configuration** (V1 `ConflictConfiguration`,
  resolved-flag documents keeping revisions): GAP-F07 (family replication
  semantics), per `docs/parity/ps-03-families-sweep.md` PB-3027.
- **Revision includes and collection statistics** (PB-3025 remainder):
  unowned; next families/query packet.
- **Start-from-etag and revision-specific transfer semantics**: GAP-F07 +
  replication receipts (PB-3028).
- **Content-plane reference counting against disposed digests**: a revert
  re-put does not verify the digest still has staged content (V1's revert
  does not either, `AttachmentsStorage.cs:611-652`); disposal while
  snapshots still reference a digest can orphan references. Owner: the
  GAP-F01 follow-up / backup packet that owns disposal policy.
- **Bin cleaner scheduling**: the host/session layer owns when
  `read_revisions_bin`/`stage_revisions_bin_clean` run (V1's
  `CleanerFrequencyInSec`); no engine-side scheduler exists.
- **Replication of the new records**: the configuration and side-car
  records replicate as whole reserved documents under generic LWW like all
  family state; family-specific merge semantics are GAP-F07's. No
  multi-region claim is made.

## Proposed collision actions (parent ratification)

- **CA-1 (PROPOSED, not executed)**: engine write-path hook for automatic
  snapshot-on-put with per-update policy application, in `src/engine/**`
  following the SM-02 schema-validation hook pattern, calling this
  module's snapshot operation. Frozen to this packet; requires a parent
  collision commit.
- **CA-2 (executed, commit `5f77bf8`)**: `src/families/state.rs` — the two
  reserved kind keys (`REVISION_CONFIGURATION_KIND`,
  `REVISION_SNAPSHOT_KIND`) and their `kind_of_record` arms, the namespace
  rule GAP-F01's warden repair established. 24 added lines; no behavior
  change without this module.
- **CA-3 (executed, commit `919d334`)**: `tests/families/main.rs` — the
  one-line module declaration for `revisions_public.rs`, following the
  directory's wiring convention.

## Receipts

Worktree `.worktrees/terrabasev2-gapf05`, toolchain pin honored
(`unset RUSTUP_TOOLCHAIN` before every cargo command).

| Command | Exit |
| --- | --- |
| `cargo fmt --all -- --check` | 0 |
| `cargo check --workspace --locked` | 0 |
| `cargo test --workspace --locked` (dev profile) | 0 — one fully green run: 1225 passed / 0 failed across 45 suites |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` | 0 |
| `cargo doc --workspace --no-deps --locked` | 0 |
| `python3 scripts/validate_parity.py --check` | 0 |
| `python3 scripts/test_parity_tooling.py` | 0 |
| `python3 scripts/test_v1_citations.py` | 0 |
| `python3 scripts/test_secret_scan.py` | 0 |
| `python3 scripts/check_license.py` | 0 |
| `python3 scripts/check_benchmark_receipts.py` | 0 (9 receipts, discipline held) |
| `python3 scripts/test_benchmark_receipt_discipline.py` | 0 |
| `./scripts/check_secrets.sh` | 0 |
| `validate_v1_citations.py --mode strict` with the V1 root and workspace root | 0 |
| `cargo deny check` | unrun — cargo-deny is not installed on this host |

### `scripts/check.sh` — six attempts, honestly recorded

No single invocation of the full script finished green, for two distinct
reasons, neither in this packet's diff (`git diff --name-only da0df43..HEAD`
contains no `src/benchmarks/**` path):

- Runs 1, 3, 5, and 6 (exit 101): the pre-existing
  `benchmarks::tests::a_self_comparison_reads_as_parity`
  (`src/benchmarks/tests.rs:481-493`) compares two real wall-clock
  throughput samples of a workload against itself and expects `Stable`;
  under this host's concurrent fleet load (load average ~70 during the
  runs) it read `Worse` twice and `Better` twice — both wrong directions,
  which is noise, not a regression. In isolation the test passed 3/3
  consecutive runs, and the dev-profile workspace suite passed it in the
  dedicated green run above.
- Run 4 (exit 1): every stage green — fmt, check, dev tests (the timing
  test passed), release tests, clippy, doc, all python stages, secret
  scan — and failed only at the final V1-citation audit because the
  script's workspace-root auto-resolution does not account for worktrees
  one level below the main checkout; that audit passes with
  `TERRASCALE_WORKSPACE_ROOT` set (exit 0 above).
- Run 2 was cut by my own wrapping 590 s shell timeout while still
  compiling under load, and one release attempt was spoiled when I killed
  a live `server_uds` test helper process I mistook for an orphan of that
  killed run — that failure is mine and is disclosed here, not counted
  against the tree.

Disposition for the parent: the packet's own receipts are complete and
green (the table above plus the in-crate and integration suites); the
script-level green run is blocked by a pre-existing load-sensitive test
outside this packet's write scope (`src/benchmarks/**` is frozen to the
benchmark lane) plus the script's worktree workspace-root resolution. The
flake determination needs the parent's cross-packet view. (Update,
repair loop 1: with both env vars set on the command line, a re-run of
`scripts/check.sh` finished green end to end — receipt in the repair
section below — corroborating the load-noise determination; the
workspace-root candidate fix for worktrees three levels deep remains a
parent action.)

Red-phase receipt: before the implementation existed, the test suite
compiled with 36 errors — the unresolved GAP-F05 imports and the missing
`FamilyStore` methods (`stage_revisions_configuration`,
`read_revisions_configuration`, `stage_revision_snapshot`,
`read_revision_snapshots`, `stage_revert_to_contract`,
`stage_enforce_revision_policy`, `read_revisions_bin`,
`stage_revisions_bin_clean`) — exit 101. That is the
discriminating red: the tests failed for exactly the missing contract, not
for an unrelated reason. (The handoff record listed seven methods;
inspection caught the transcription slip — the module exposes eight
`FamilyStore` operations, all eight named above. `read_revisions_configuration`
was added to this list in repair loop 1.)

## Repair loop 1 (2026-08-28) — gate findings closed

Inspector verdict GO (two MINORs, three NOTEs); warden verdict FAIL on
one MAJOR with the repair fully specified and verified. Every MAJOR and
every actionable MINOR is closed below, each red-then-green; NOTE-level
items are closed where cheap and disclosed where they belong to the
parent. Repair commits: `ad46482` (red tests), `ca23078` (fixes), plus
this record.

### Findings -> fixes

1. **Warden MAJOR — the side car desynced from the log and grew without
   bound under the W3-F per-document policy.**
   `RevisionRecord::push` trims to the W3-F per-document policy
   (`src/families/state.rs:1042`), and `apply_policy`'s four early
   returns reap nothing, so `stage_revision_snapshot` — the one GAP-F05
   write that did not re-unify the two records afterwards — left
   `family:revision-snapshots/<doc>` growing one entry per write whenever
   the collection policy had nothing to drop (warden repro: W3-F
   `RevisionPolicy(1, Some(2))` + collection minimum 100, seven
   snapshots, log 2 / side car 7). **Fix:** `side.retain_log(&log)`
   unconditionally after the `apply_policy` call in
   `stage_revision_snapshot`, matching `stage_revert_to_contract` and
   `stage_enforce_revision_policy`. The record's "reaped on every write"
   claim now holds for every GAP-F05 write; W3-F-native commands
   (`ForceCreate`/`Revert`/`Configure`) still do not know the side car —
   the next GAP-F05 write reaps what they trimmed (their lane is frozen
   to W3-F).
2. **Warden MINOR — u64 policy limits silently mutated through the
   reserved-record codec.** `encode`'s overflow clamp mapped any count
   above `i64::MAX` to `i64::MAX`, so staged `u64::MAX` limits read back
   changed, with no error anywhere. **Fix:** the typed constructor now
   refuses count limits above the record's i64 ceiling
   (`MAX_RECORDED_COUNT`, the schemas module's `MAX_INTERCHANGE_INTEGER`
   discipline): construction rejects instead of encoding a value storage
   cannot return, and `i64::MAX` itself round-trips exactly. The snapshot
   side-car codec's identically-shaped clamp on revision numbers stays:
   one push creates one revision, so a revision number above the ceiling
   is unreachable (and the counter saturates).
3. **Warden MINOR — duplicate bin-clean candidates staged an
   uncommittable transaction and an inflated count.** `try_new` accepted
   a repeated document; the stage then cleared the same record repeatedly
   (a commit-time `Validation` rejection) and reported `purged = 3` for
   one document. **Fix:** `RevisionsBinCleanWrite::try_new` refuses
   repeated candidates (`InvalidFamilyPayload`), fail-closed like the
   module's other payload validations; V1's cleaner feeds distinct ids
   from one bin page.
4. **Warden NOTE — `removed`/`removed_by_policy` under-counted the W3-F
   post-drain trim.** **Fix:** `apply_policy` counts the post-drain
   `trim_to_policy` removals alongside the collection drain; `HasMore`
   stays driven by the cap alone (`RevisionsStorage.cs:833`), never by
   the W3-F trim. On every maintained log the public paths reach
   `apply_policy` at or below the W3-F maximum, so the accounting change
   only affects records that outgrew their policy — the defensive trim's
   reason to exist; the unit test builds one by hand.
5. **Inspector MINOR + warden NOTE — two V1 citation line pins were
   wrong at `fa99b925` (claims true, pins off).** The 30-day bin-age
   default is `RevisionsBinConfiguration.cs:27` (was cited `:63`, which
   is inside `GetHashCode`; the table row's `:40-63` spanned
   Equals/GetHashCode/ToJson and now reads `:16-36` — `Disabled` `:16`,
   age `:27`, cadence `:36`), and
   `RevisionsCollectionConfiguration.Disabled` is `:12` (was `:19`,
   inside `Equals`). Fixed in this record and in both module doc
   comments. The prose-pin gap in
   `validate_v1_citations.py --mode strict` (it checks bracketed tokens,
   not prose `File.cs:NN`) is disclosed for the parent's tooling lane.
6. **Inspector NOTE — "seven FamilyStore operations" was a transcription
   slip.** The module exposes eight; the red-phase receipt above now
   names all eight (`read_revisions_configuration` was missing from the
   original list).
7. **Inspector NOTE + warden NOTE — record wording.** D2 now discloses
   the double-unchanged-snapshot revert nuance. The unowned PB-3025
   remainder (revision includes, collection statistics) remains flagged
   for the parent to assign an owner at ratification — a builder cannot
   open tickets.
8. **Warden NOTE — parent-level tooling, no packet action.** The
   benchmarks self-comparison flake and the `check.sh` worktree
   workspace-root resolution stay parent determinations; the flake
   reproduced once in this loop's first workspace run and passed in the
   second (both recorded below), and `scripts/check.sh` is outside this
   packet's write scope.

### Tests — red first on the handed-off tree, then green

| Regression test | Finding | Red (unfixed) | Green (fixed) |
| --- | --- | --- | --- |
| `snapshot_side_car_follows_the_w3f_policy_trim` | MAJOR | side car 7, expected 2 | log 2 / side car 2 |
| `configuration_limits_round_trip_exactly_and_refuse_the_codec_ceiling` | MINOR u64 | `Ok(())` where refusal was required | ceiling refused; `i64::MAX` round-trips exactly |
| `bin_clean_refuses_duplicate_candidates` | MINOR duplicates | `Ok(())` where refusal was required | refused |
| `apply_policy_counts_every_removal_including_the_w3f_trim` | NOTE counts | removed 1, expected 3 | removed 3; side car 2 |

Red receipt: `unset RUSTUP_TOOLCHAIN && cargo test --locked --lib
families::revisions::tests` — exit 101, **18 passed / 4 failed**, each
failure on exactly its finding's assertion (commit `ad46482` contains
only the four tests). Green receipt: exit 0, **22 passed / 0 failed**
(18 prior + 4 new), and `cargo test --locked --test families` — 25
passed / 0 failed.

Mutation re-check (the warden's mutation 4 — deleting both `retain_log`
calls inside `apply_policy` left all 18 packet tests green): with the
repair tests present, that mutation now turns
`apply_policy_counts_every_removal_including_the_w3f_trim` red (its
side-car assertion is the only one the mutation can touch), and deleting
the new unconditional `retain_log` in `stage_revision_snapshot` turns
`snapshot_side_car_follows_the_w3f_policy_trim` red — both lockstep
layers now have discriminating coverage. Executed on a scratch copy and
reverted; the committed tree is the fix.

### Receipts (repair loop 1)

| Command | Exit |
| --- | --- |
| `cargo fmt --all -- --check` | 0 |
| `cargo check --workspace --locked` | 0 |
| `cargo test --workspace --locked` (dev profile), run 1 | 101 — the sole failure is the pre-existing load-flaky `benchmarks::tests::a_self_comparison_reads_as_parity` (`src/benchmarks/tests.rs:492`, read `Better`, expected `Stable`, load average ~40); the packet diff (`da0df43..HEAD`) touches no `src/benchmarks` path |
| `cargo test --workspace --locked` (dev profile), re-run | 0 — fully green: 45 suites, 1229 passed / 0 failed (1225 at handoff + the 4 repair tests); the flake also passed isolated |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` | 0 |
| `TERRABASE_V1_ROOT=<pin> TERRASCALE_WORKSPACE_ROOT=<workspace> ./scripts/check.sh`, run 1 | 101 — fmt, check, and the full dev-profile suite green (70 green result lines including doc-tests); died in the release-profile suite on the same benchmark flake (`Better` vs `Stable`) |
| `TERRABASE_V1_ROOT=<pin> TERRASCALE_WORKSPACE_ROOT=<workspace> ./scripts/check.sh`, run 2 | **0 — the packet's first complete script-level green run** ("TerraBaseV2 foundation checks passed"): every stage including the strict V1 citation audit with the pinned root; `cargo-deny` recorded unrun (not installed) |

`parity/v1-test-gaps.json` was hash-watched before and after each python
receipt stage after the warden observed it rewritten in their session;
it was untouched in this loop (same SHA-256 before and after every run).

## Non-claims

- No claim that revisions are created automatically on document writes;
  until CA-1 is ratified and lands, snapshots are explicit operations.
- No claim of replication, multi-region, or convergence behavior for any
  revision record.
- No performance claim of any kind; no benchmark receipt exists for this
  packet.
- No claim that `RevisionPage`/`RevisionCommand` (the W3-F paths) changed;
  they are untouched and their tests pass unmodified.
- No claim that revision reads enforce configuration: historical reads
  serve stored revisions regardless of the collection's current policy,
  matching V1's serve-stored-revisions behavior.
- No claim that a reverted attachment reference's bytes still exist in the
  content plane (see the deferred reference-counting item).
