# W4-REPLICATION R-1 implementation record

Status: implementation record for phase R-1 of the W4-REPLICATION packet,
2026-08-24. Authority: `docs/design/w4-transfer-replication-contract.md`
("Replication and multi-region contract", "Public type ownership",
"W4-REPLICATION red tests") and `docs/adr/0004` (accepted D-1..D-4). Worktree
`terrabasev2-w4-replication`, branch `fleet/w4-replication`, based on
`762775c`.

Phase scope (from the charter's dispatch record): R-1 is the vocabulary,
topology, transport, status, tenant-isolation, cursor, catch-up,
idempotence, rejoin, and anti-entropy phase. R-2 owns the acknowledgement
lattice above `Local`, convergence, member/region loss, conflicts, and
fencing; R-3 owns the three-Region fault lane and `ReplicationReceipt`
artifacts. This record resolves no parity row and makes no capability claim
beyond the tested behavior.

## What was built

`src/replication/` implements the frozen W4-0 declaration tree:

- `mod.rs` — the public seam types with their real internal
  representations (the W4-0 `PhantomData` placeholders are gone for the
  implemented types), `Default for AcknowledgementLevel` (`Local`,
  ADR-0004 D-2), and two `compile_fail` doc proofs of the seam rules: a
  local commit receipt exposes no acknowledgement accessor, and a
  `ReplicationStatus` value cannot be turned into a `Convergence`
  observation.
- `identifier.rs` — `RegionId`/`MemberId` validation restating the crate's
  logical-identifier rules (the `logical_identifier!` macro is private to
  `src/types.rs`).
- `topology.rs` — the builder-style `TopologyView` and `ReplicationConfig`.
- `transport.rs` — the crate-private deterministic in-process link
  doubles.
- `member.rs` — the crate-private replication member driver, the
  `ReplicationCursor` implementation, and the `synchronize` anti-entropy
  driver.
- `status.rs` — the `ReplicationStatus` health derivation, with the R-2
  convergence TODO recorded at the top.
- `tests.rs` — the eight scope-bound listed red tests of the R-1 list plus
  seven focused extras; the two public-construction listed tests live in
  `tests/replication/` with four focused extras.

All ten listed R-1 test names are present and green; none was weakened.

## The public seam in R-1

- `RegionId::try_new` / `MemberId::try_new` / `as_str` / `Display`.
- `TopologyView::new` with the `with_region`, `with_member`, `with_owner`
  builder chain and the `validated` admission step; read accessors for
  regions, members, seats, failure domains, and per-range ownership.
- `ReplicationConfig::try_new(view)`, `with_acknowledgement(level)`,
  `acknowledgement()`, `topology()`. The acknowledgement level is an
  explicit request input with a `Local` default (D-2); R-1 names levels
  and evaluates none above `Local`.
- `ReplicationCursor::start(member)` and the progress accessors. The
  member-derived cursor is crate-private and journal-backed.
- No public type was added, renamed, or removed; `AcknowledgementReport`,
  `Convergence`, `ReplicationReceipt`, and `ConflictRecord` keep their
  declaration-only content with named R-2/R-3 reservations.

## Topology validation (D-1)

`TopologyView` is caller-supplied membership input and validates exactly
the data-plane invariants, with every rejection typed as
`ReplicationError::InvalidTopology`:

- identifier text follows the crate's logical-identifier rules;
- a Member is seated once, in a Region the view names;
- a Failure Domain groups Members whose correlated loss is one failure and
  therefore never spans Regions;
- a data range has exactly one owning Member (re-assignment is a
  control-plane transition, not a builder step);
- a servable view names at least one Region and seats at least one Member
  per Region.

## The member driver

One `ReplicationMember` owns one durable `Engine` opened from its own
`EngineConfig` (so `restart`/`rejoin` reopen the same location), applies
one validated `ReplicationConfig`, and moves committed operations through
the transport. The write path is admission-fenced per D-1: a member
commits a write only for a data range whose write path its current view
assigns it, so a deposed owner's new writes fail locally. R-1 refuses any
acknowledgement level above `Local` with a typed error rather than
implementing or silently downgrading it.

Catch-up is request/response over the transport: the follower asks for a
tenant's published commits (`Engine::tenant_commits`), the source serves
reconstructable write sets with its `journaled_high_water`,
`tenant_sequence`, and `servable_history_start` facts, and the follower
applies each commit through the Engine transaction seam
(`begin` → `put`/`delete` → `commit(LocalDurability::Synced)`) under the
original `OperationId`. Delivery is at-least-once by design.

Idempotence and identity reuse. A delivered commit is always staged and
committed — never skipped on a pre-check — because the engine's operation
ledger is the component that binds an operation identity to its write set:
the same write set replays to the original receipt, and a different write
set under a live identity is rejected as `CommitError::OperationIdReuse`,
which the driver surfaces as `MemberError::ApplyFailed` and never skips
silently. A pre-apply `resolve_commit` is used only to classify the
outcome for the caller's accounting.

Deletes replay with the journal's `version_at_delete` as the expected
version (zero means the provable no-op). Because the public Engine seam
derives a delete's collection from the snapshot rather than accepting the
journal's recorded key, the driver adds a typed `KeyMismatch` failure when
the identifier currently lives in a different collection than the source
deleted from — the storage-level exact-key replay rule's intent, kept
within the frozen engine seam.

Tenant isolation. Both catch-up seams are scope-gated: serving answers
only requests whose tenant matches the serving scope, and applying admits
only batches whose tenant matches the applying scope. A mismatch is
denied with a typed `MemberError::CrossTenant`, recorded in a bounded,
observable denial log, degrades the member's health observation, and
transmits nothing.

Cursors. `ReplicationCursor` is derived from the member's own
publication-history journal — the ground truth of what that member durably
applied — so it is expressed in the member's own storage-sequence space
and survives an engine restart unchanged. Advisory in-memory progress
(expressed in the serving member's sequence space) exists to keep
requests incremental; it never survives a restart, and resumption after a
restart re-receives the tenant history and resolves already-applied
operations through the ledger. The follower's cursor cannot be handed
back to the source as a resume position: the two sequence spaces differ,
and doing so would silently skip history, so resumption is by durable
operation identity.

Rejoin. `rejoin` reopens the location (the identity and format check: a
location holding another storage format, or bytes that do not verify,
fails the reopen) and then compares the freshly journal-derived cursor
with the cursor the member left with. A regression means the durable
history went backwards; the member is reported through a typed
`RejoinRejected` fact and is not admitted.

Anti-entropy and gaps. `synchronize` drives bounded rounds of
request/pump/serve/pump/apply until the follower's advisory progress
reaches the source's last-known per-tenant high water. A round that
received no batch proves nothing and is never reported as caught up. A
cursor the source cannot serve below either falls back to a full catch-up
(when the flag names the follower's own cursor) or fails with the typed
`GapUnservable` error carrying the cursor and the servable bound; a gap is
never silently skipped.

Health. `member_health` derives one `ReplicationStatus` from the member's
own facts with a fixed precedence (unavailable, degraded, stale,
responsive, active, assigned). It reports health only and there is no
path from any status value to a `Convergence` observation.

## The transport doubles

`ReplicationNetwork` holds one bounded `DirectedLink` per member pair.
Per direction: `delay` holds sent frames back in order until an explicit
`pump` releases them, `reorder` releases them newest-first, `duplicate`
delivers every frame twice, `drop_next` loses a bounded number of frames
in transit, and `partition` loses everything. Every queue is bounded
(`MAX_LINK_FRAMES`), a send beyond the bound is a typed `LinkFull` error,
and each fault effect is observable through per-direction counters so a
test can prove its fault fired. There are no threads, async runtimes, or
timers anywhere in the module; time moves only in `pump`.

## Limits and non-claims

- No acknowledgement level above `Local` is evaluated, no
  `AcknowledgementReport` or `Convergence` content exists, no conflict
  policy runs, and no leader election or fencing transition is
  implemented — those are R-2.
- No three-Region lane, `ReplicationReceipt`, or RPO/RTO/convergence/lag
  measurement exists — those are R-3. This record states no number and no
  production topology capability.
- `ReplicationMember`, the transport, and the catch-up protocol are
  crate-private by contract. The only current driver is the test lane;
  the items carry named dead-code allowances until the R-2/R-3
  acquisition work lands.
- The member's catch-up re-receives full tenant history after a restart
  (the ledger resolves the prefix). This is correct and bounded per round
  but not incremental; see the proposed collision action below.
- The R-1 apply path stages every redelivered commit through a
  transaction because the public resolution seam carries no write-set
  digest; correctness first, cost noted.

## Proposed collision actions

- W4-CA-8 (proposed): extend `Engine::resolve_commit` (or the
  `DurableResolution::Committed` receipt) to carry the operation's
  write-set digest, or add a crate-private digest lookup beside it, so a
  follower can cheaply distinguish "same write set, already applied" from
  "different write set under a live identity" without staging the
  redelivery. Touches frozen `src/engine/**`/`src/storage/**`; a builder
  cannot do it.
- W4-CA-9 (proposed): a durable, source-sequence progress record for
  replicated applies (the follower journal records its own sequence
  only), so post-restart catch-up can resume incrementally instead of
  re-receiving the tenant prefix. Same frozen-path constraint.

## Verification

Run from the worktree at the tip of this phase:

- `cargo fmt --all -- --check`
- `cargo check --workspace --locked --offline`
- `cargo clippy --workspace --locked --offline --all-targets -- -D warnings`
- `cargo test --workspace --locked --offline`
- `python3 scripts/validate_parity.py --check`

The R-1 test names and their homes:

- `local_commit_receipt_contains_no_region_or_member_facts` — in-crate
- `single_region_single_member_write_reads_back_and_survives_restart` —
  in-crate
- `replication_request_names_an_explicit_acknowledgement_level` —
  `tests/replication/`
- `replication_status_reports_health_separately_from_convergence` —
  `tests/replication/`
- `tenant_isolation_probe_denies_cross_tenant_replication_and_is_observable`
  — in-crate
- `delayed_and_reordered_replication_replays_idempotently_to_the_contract_outcome`
  — in-crate
- `duplicate_delivery_applies_each_operation_once` — in-crate
- `member_rejoin_checks_identity_and_catches_up_without_duplicates` —
  in-crate
- `restarted_member_resumes_from_its_durable_replication_cursor` —
  in-crate
- `anti_entropy_repairs_a_lagging_member_after_a_gap` — in-crate

Focused extras (in-crate unless noted):
`operation_id_reuse_with_a_different_write_set_is_a_typed_failure`,
`a_cursor_below_servable_history_falls_back_to_full_catch_up_never_silently_skips`,
`member_health_reports_status_only_and_never_convergence`,
`partitioned_link_blocks_catch_up_until_healed`,
`bounded_links_reject_frames_beyond_their_bound`,
`rejoin_rejects_a_location_whose_format_does_not_verify`,
`write_admission_is_per_range_and_levels_above_local_are_refused_in_r1`,
`topology_identifiers_match_the_logical_identifier_rules`
(`identifier.rs`), and in `tests/replication/`:
`region_and_member_identifiers_validate_as_logical_identifiers`,
`topology_views_validate_membership_ownership_and_failure_domains`,
`replication_config_validates_its_topology_and_policy_bounds`,
`replication_cursor_starts_empty_and_reports_only_progress_facts`.

## R-2 entry points left ready

- The acknowledgement request naming exists end to end
  (`ReplicationConfig::with_acknowledgement`, the member's typed refusal
  above `Local`); R-2 replaces the refusal with the lattice evaluation.
- `status.rs` carries the convergence TODO; `Convergence` remains a ZST.
- The transport doubles are ready for three-Region topologies and the
  fault classes the lane needs; nothing in them assumes two members.
