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
every write; reads hide side-car entries whose revision left the log).

Tests: 18 focused inline tests (`#[cfg(test)] mod tests` in the module) and
4 public-construction integration tests
(`tests/families/revisions_public.rs`). Red-then-green: the full suite was
written first and compiled-failed on exactly the missing API (receipt
below), then the implementation turned it green.

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
     configuration (30-day default, `RevisionsBinConfiguration.cs:63`) and
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
  `RevisionsCollectionConfiguration.cs:19`); unconditional auto-snapshot in
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
unambiguous. A snapshot staged in the same transaction as a document write
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
| Bin configuration: 30-day default age, cleaner cadence, disabled switch | `.../RevisionsBinConfiguration.cs:40-63` |
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
| `cargo test --workspace --locked` | 0 (1225 passed / 0 failed across 45 suites, dev profile) |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` | 0 |
| `TERRABASE_V1_ROOT=... ./scripts/check.sh` (run 1) | 101 — one flake: `benchmarks::tests::a_self_comparison_reads_as_parity` read `Worse` on real wall-clock self-comparison samples under concurrent fleet load; this packet touches nothing near `src/benchmarks/**` |
| single-test re-run of that test | 0 (passed) |
| `./scripts/check.sh` (run 2) | timeout of the wrapping 590 s shell (still compiling under fleet load); re-run without the wrapper recorded below |
| `./scripts/check.sh` (run 3) | see the packet record addendum; run in background to escape the tool timeout, result recorded in the structured output |

Red-phase receipt: before the implementation existed, the test suite
compiled with 36 errors — the unresolved GAP-F05 imports and the missing
`FamilyStore` methods (`stage_revisions_configuration`,
`stage_revision_snapshot`, `read_revision_snapshots`,
`stage_revert_to_contract`, `stage_enforce_revision_policy`,
`read_revisions_bin`, `stage_revisions_bin_clean`) — exit 101. That is the
discriminating red: the tests failed for exactly the missing contract, not
for an unrelated reason.

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
