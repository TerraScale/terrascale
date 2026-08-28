# Time-series execution decision record (GAP-F04, ticket #73)

Status: decision record, written before the code (decision-first, then
test-first). Owner scope: rollup generation, retention cleanup, and bounded
cursor-stable paging for the time-series family — the three capabilities
`docs/families/w3-families.md` recorded as limits ("retention cleanup itself
does not run, so pre-horizon entries stay stored until the streaming packet
owns their deletion"; "Rollup policies are stored, validated, and reported;
rollup generation does not run"; "Time-series pages are bounded by the
record's document size, not by an explicit cursor or backpressure protocol",
`docs/families/w3-families.md:318-326`).

V1 evidence is quoted against the pinned read-only checkout at
`/home/mariogk/Projects/TerraScale/.worktrees/terrabase-v1-fa99b925-readonly`
(sweep commit `fa99b9257776dbe625e6a2388c74c9ce916637ee`). No parity row
status changes here; the WS-PARITY proposals for the touched rows are listed
at the end.

## D1 — Execution owner: engine-internal module, host-driven tick

**Decision.** Rollup and retention execution is engine-side: a new
module `src/families/timeseries/` exposes typed step operations
(`rollup_series`, `enforce_retention`, and `run_series`, which drives both in
V1's order). Each step opens its own Engine transaction, stages through the
family record path, and commits — the SM-03 migration drive shape
(`src/families/migrations/mod.rs:25-31`: "typed step operations the host
drives ... each page its own committed transaction"). The tick — *when* a run
happens — is not owned here: the engine has no background worker and no
looping seam, so the host (the C# API host's supervisor, or a future
engine-owned maintenance lane) calls `run_series`/`run_tenant` on a schedule,
handing in `now_ms`. V1's default check frequency is 10 minutes
(`TimeSeriesConfiguration.cs:13`,
`DefaultPolicyCheckFrequency = TimeSpan.FromMinutes(10)`); the interval is
caller policy in V2, not engine state.

**Evidence.**

- V1 runs rollups and retention inside the server, not the client and not the
  host: `TimeSeriesPolicyRunner : BackgroundWorkBase`
  (`src/TerraBase.Server/Documents/TimeSeries/TimeSeriesPolicyRunner.cs:20-28`)
  is constructed per database from the database record (`:41-90`) and its
  `DoWork` loop waits one check frequency, then runs `RunRollups` followed by
  `DoRetention` (`:92-105`). Execution is database behavior; the client SDK
  only configures the policy (`ConfigureTimeSeriesPolicyOperation.cs`).
- GAP-D01's dividing test puts rules that must hold inside one atomic commit
  engine-side ("only the engine owns the transaction",
  `docs/sessions/gapd01-ownership-decision.md:39-42`). A rollup run rewrites
  the series record's derived sections and a retention run drops points from
  it; both are record writes whose atomicity, conflict behavior, and
  publication must ride one Engine transaction. A host-side executor would
  assemble writes it cannot validate atomically — the same rejected
  alternative as GAP-D01 class 1 (host-side queue: "two validators,
  partial-commit window across TBEP", `gapd01-ownership-decision.md:54`).
- The engine is synchronous and has no wall clock in its seams: GAP-R02
  explicitly rejected time-driven retention for the tombstone watermark
  because "the correctness condition is not time"
  (`docs/replication/tombstone-retention-watermark.md:197-203`), and family
  logical times are caller-supplied epoch milliseconds
  (`src/families/state.rs:108-116`). So V1's `BackgroundWorkBase` timer maps
  onto a caller-driven tick, exactly as the migration drive and the HiLo
  distributor (GAP-F09) already map V1 background/client behavior onto
  host-driven engine steps.
- V1 elects a single retention executor per topology: `DoRetention` returns
  immediately unless the node is first in topology
  (`TimeSeriesPolicyRunner.cs:330-333`), and `RollupOne` re-checks it before
  writing (`TimeSeriesRollups.cs:540-541`). V2's in-process single-writer
  equivalent is the Engine borrow: one `run_series` call holds the Engine
  transaction, and the Engine's operation reservation serializes commits.
  Cross-process single-writer (one host among replicas) is the replication
  owner's concern — recorded as a rider below, not claimed here.

**Rejected alternatives.**

- *Host-driven execution over TBEP (host computes windows and stages
  batch writes):* duplicates the windowing/aggregation rules in C#, gives the
  host a second validator the engine must still check, and leaves no
  engine-side conformance target — rejected for GAP-D01's class-1 reasons.
- *Engine-owned background thread:* the engine is synchronous by design
  (`src/engine/mod.rs` documents synchronous sink invocation on the
  committing thread); adding a thread changes the engine's concurrency
  contract far beyond this packet and would couple retention to a clock the
  engine does not own.
- *Read-time materialization (compute rollups during reads, store nothing):*
  unbounded read cost over the whole series per page, and V1 stores rollups
  as real series (`RollupOne` appends into the derived series,
  `TimeSeriesRollups.cs:604`) precisely so reads stay cheap.

## D2 — Incremental series encoding

**Decision.** Derived rollup series are encoded *inside the series' own
record* as a `rollups` section: a map from rollup name to its aggregated
entries, stored beside the raw entries in the one reserved-record value.
The section is the incremental state:

- **Windowing.** A window of `window_ms` containing timestamp `t` is
  `[floor(t/w)·w, floor(t/w)·w + w)` using Euclidean flooring, so negative
  epoch timestamps floor correctly. This is V1's tick alignment adapted to
  milliseconds: `NextRollup` computes `timespan.Ticks * (integerPart + 1)`
  (`TimeSeriesRollups.cs:960-972`). A window is **complete** when its end is
  at or before the run's `now_ms`; only complete windows are aggregated —
  V1's `rollupEnd = NextRollup(_now, policy) - aggregation - 1ms` boundary
  (`TimeSeriesRollups.cs:542`).
- **Aggregation.** The aggregated point's timestamp is the window start —
  V1's `SingleResult { Timestamp = rangeSpec.Start, ... }`
  (`TimeSeriesRollups.cs:908-920`). Each value column aggregates with the
  policy's named [`Aggregation`] (Min/Max/Sum/Mean/Count), one value per raw
  column, so rollup entries keep the raw arity. (V1 instead fixes a six-stat
  rollup — first, last, min, max, sum, count — per column
  (`TimeSeriesRollups.cs:667-676`); the W3-F payload contract landed one
  named aggregation per policy (`src/families/payloads.rs:859-872`), and this
  packet executes the landed contract rather than changing it.)
- **Idempotent re-runs.** A run recomputes every complete window
  deterministically and *replaces* the section. Because window boundaries and
  aggregations are pure functions of the raw entries and the policy, a re-run
  without new raw points computes the identical section and stages nothing
  (the run reports `changed = false` and never commits) — the deterministic
  counterpart of V1's change-vector short-circuit, where an already-merged
  rollup is skipped and the state re-marked
  (`TimeSeriesRollups.cs:529-537`).
- **Flat, not chained.** Each rollup policy aggregates from the **raw**
  series. V1 chains policies ("Each policy will create a new time-series
  aggregated from the previous one",
  `TimeSeriesCollectionConfiguration.cs:16-18`), but the landed V2 payload
  carries an unordered list of named rollups with no chain position
  (`src/families/store.rs:506-519` validates only uniqueness and positive
  windows). This is an **accepted difference**, recorded here, not silent:
  chaining needs a policy-order contract the payload does not yet carry.
- **Reads.** `TimeSeriesRead` gains an optional rollup selector and bounded
  paging (D4); a read naming a rollup absent from the stored policy is the
  typed `TimeSeriesPolicyInvalid`.

**Why same-record rather than V1's derived series names.** V1 names rollup
series `raw@policy` (`GetTimeSeriesName`,
`TimeSeriesCollectionConfiguration.cs:283-289`). V2 series names accept `@`
(`validate_series_name` rejects only control characters and `/`,
`src/families/payloads.rs:67-73`), so a derived record keyed `name@rollup`
would be indistinguishable from a user series someone actually named
`name@rollup` — the same-tenant reserved-record ambiguity W3-F already
records as a limit (`docs/families/w3-families.md:286-297`). Encoding
sections inside the owning record also keeps one series' raw and derived
state under one record version and one family write, and needs no new
reserved-state layout. Cost: the record value grows with each rollup policy —
bounded by the same document-size bound that already bounds the raw entries,
and no performance claim is made.

**Why recompute-and-replace rather than V1's marker table.** V1 tracks the
next rollup per series in a dedicated rollup table
(`RollupState`/`NextRollupIndex`, `TimeSeriesRollups.cs:34-59`) so a run
aggregates only past the marker. V2's record is a single value with no
segment index to mark; a marker would be new reserved state whose only
consumer is this module. Recompute-and-replace is O(series) per run over a
record-bounded value, is idempotent by construction, and needs no extra
state. The incremental guarantee that matters — a re-run after new points
adds only the new windows' points and leaves prior points byte-identical —
holds because replacement is deterministic. **Recorded limit:** a raw append
landing (out of order) in a window *older than the oldest complete window*
contributes to the next recompute (the section is fully replaced, so unlike
a pure forward cursor it is *not* lost); the residual gap is none for
correctness — the only cost is re-derivation, which is the accepted
trade.

## D3 — Retention and the GAP-R02 tombstone watermark

**Decision.** Retention cleanup observes the tombstone retention watermark
and never advances it. The rule set:

1. **Horizon.** With `retention_ms` configured and a non-empty series, the
   horizon is `newest_raw_timestamp − retention_ms` — exactly the read
   path's existing formula (`src/families/store.rs:455-458`). The run drops
   raw entries with timestamps strictly below the horizon and trims every
   rollup section to the same horizon (V1 applies retention to each policy's
   series too: `ApplyRetention` runs for the raw policy and then per policy,
   `TimeSeriesPolicyRunner.cs:354-359`).
2. **Pending-rollup guard.** A raw entry is deletable only when every
   configured rollup policy's window containing it is complete
   (`window_end <= now_ms`). This is V1's `HasPendingRollupFrom` rule —
   retention skips a series-date a rollup has not yet covered
   (`TimeSeriesPolicyRunner.cs:407-408`). The drive order makes the guard
   tight: `run_series` executes rollups before retention, V1's `DoWork`
   order (`TimeSeriesPolicyRunner.cs:101-103`), so after the rollup half
   every complete window is aggregated and the guard reduces to
   window completeness.
3. **Tombstone evidence window (the GAP-R02 interaction).** A run refuses to
   touch a series whose owning document's deletion evidence the watermark
   still retains: if `TombstoneRetention`'s retained set holds any
   `DeletionEvidence` fact naming the owning document, the pass **skips the
   series** (nothing staged) and discloses the skip in the receipt. The
   retained set *is* the evidence window — facts below the retention floor
   are already discarded by the watermark itself
   (`TombstoneRetention::hold` drops below-floor facts,
   `src/replication/retention.rs:556-574`), so a fact present in the set is
   a fact whose window still covers the document, held for the slowest
   consumer (D1 of the watermark record: "retention floor <= every consumer
   floor", `docs/replication/tombstone-retention-watermark.md:61-77`).
   Deleting series points out from under a still-covered tombstone would
   break the same family-level fencing GAP-F07 established for counters (a
   live record that keeps the deleted name fenced), which GAP-R02 explicitly
   leaves to the family plane: "The counter-record tombstone GAP-F07 D2 mints
   ... is a family-level fact and rides the generic put/delete plane; this
   record governs how long such evidence survives, not its encoding"
   (`docs/replication/tombstone-retention-watermark.md:267-270`).
4. **No watermark transitions.** The retention pass calls only read-side
   watermark operations (`deletion_evidence_above`). It never advances the
   floor, never declares a history bound, and never records a consumer: those
   transitions belong to the member, the backup run, and the compaction
   driver (`docs/replication/tombstone-retention-watermark.md:87-141`). A
   time-series retention delete is a *value* delete inside a live record; the
   record itself is removed only when it holds nothing (the existing
   `write_or_clear` empty rule), and that removal rides the ordinary Engine
   publication, where any document-level evidence the journal mints is the
   watermark's to govern — not this module's.
5. **Skip is disclosed, never silent.** The receipt reports
   `skipped_document_evidence` series by name, the same disclosure
   discipline as the watermark's refusal log and `below_floor_skipped`
   counts (`src/replication/retention.rs:341-363`).

**Rejected alternative — sequence-space gating.** Binding the horizon to the
watermark's sequence floor (delete only points whose record version is below
the floor) conflates two spaces: the floor bounds *deletion evidence*
survival for consumers, not value visibility. It would either never delete
(floor zero retains everything) or couple series cleanup to replication
progress the series does not depend on. The document-fact rule above is the
one GAP-R02 actually defines.

## D4 — Paging: bounded, cursor-stable

**Decision.** The family read seam gains explicit bounded paging. A
[`TimeSeriesPaging`] carries an optional **exclusive timestamp cursor**
(`after`) and a bounded `limit` (1..=`MAX_TIMESERIES_PAGE_LIMIT`, 1024 — the
query seam's `MAX_PAGE_LIMIT`, `src/query/spec.rs:44`, and the streaming
seam's batch cap, `src/streaming/mod.rs:59`). The page reports the matched
`total` before paging (the query seam's convention,
`src/query/result.rs:231`) and a `next` cursor — the last served entry's
timestamp — present exactly when more matched entries follow. Chaining
cursors serves the whole retained view in bounded pages.

**Cursor stability.** Series timestamps are unique within a record (an
append at an existing timestamp replaces it,
`src/families/store.rs:556`), so the exclusive timestamp cursor is a total,
stable order key. A later page never re-serves or skips entries because of
insertions *after* the cursor; an entry that later lands *at or before* the
cursor is not served to the resumed read — the documented stability
semantics, matching V1's range-addressed time-series reads (a read addresses
a timestamp range; older-addressed appends are not retrofitted into a range
already served). Offset paging (the revisions convention) is rejected here
because an offset is not stable against out-of-order appends: a new entry
before the offset shifts every later page, and time-series appends are not
guaranteed monotonic. Reads without paging keep the exact prior
whole-retained-view behavior, so the streaming source's existing slicing
(`src/streaming/sources.rs:727-778`) and every existing caller are
unchanged.

## Test plan (RED first)

New module tests `src/families/timeseries/tests.rs`, all driven through the
public engine and module seams (the hilo/migrations test shape), plus the
read-path paging tests at the family read seam:

- **Rollups:** window alignment (including negative timestamps), completeness
  boundary (`window_end <= now`), per-aggregation arithmetic (Min/Max/Sum/
  Mean/Count, multi-column), arity preservation, idempotent re-run (no
  change, no commit), re-run after new points (prior points identical, new
  windows added), rollup-name pruning when the policy drops a rollup.
- **Retention:** horizon deletion, pending-rollup guard (incomplete windows
  retained), rollup-section trimming, no-policy no-op, empty-record clear,
  watermark evidence skip (fact present ⇒ skip and disclose; fact gone or
  floor advanced past it ⇒ proceed), and the no-transition rule (the
  watermark's floor/bound/refusals unchanged by a run).
- **Paging:** limit validation bounds, bounded chaining over N entries,
  cursor stability (late older insert invisible, later insert visible),
  matched-total reporting, rollup-section reads with paging, unknown rollup
  name refusal.

RED evidence is recorded in this document's *Evidence* section below after
the runs; the tests exercise types that do not exist before the packet, so
the RED shape is compile-time (unresolved module/methods), recorded verbatim.

## Limits

- No V1 parity row status changes; proposals only (below).
- The tick has no production driver yet: no host loop or engine maintenance
  lane calls `run_series`/`run_tenant`. V1's 10-minute default is caller
  policy here. Rider for the integrator: SE-08's host supervision or a
  dedicated maintenance seam owns the schedule and the cross-process
  single-writer election V1 gets from first-in-topology
  (`TimeSeriesPolicyRunner.cs:330-333`).
- `now_ms` is caller-supplied; a run is only as correct as the clock it is
  handed. No time qualification is claimed (the W3-F discipline).
- Multi-node rollup convergence (two members running rollups on the same
  series) is not claimed; the family write path's conflict rules apply, and
  the replication owner owns the coordination contract.
- No performance claim; no benchmark ran (the hard-data rule,
  PD-2026-08-26-03).
- Rollup reads are exposed through the family read seam only; TBEP/host
  exposure of the execution operations is a rider for the SE-08/TBEP owners.

## WS-PARITY proposals (docs only; no `parity/` status changes)

Recorded as `docs/parity/gapf04-ws-parity-proposals.md`: PB-3019
(time-series-rollup-retention-config, executed scope), PB-3024
(time-series-errors, the rollup-selector refusal joins the typed case set),
and the streaming bounded-read rows (the family-seam half is implemented;
the streaming source is deliberately unchanged).

## Evidence

### RED (recorded before any implementation existed)

Command: `env -u RUSTUP_TOOLCHAIN cargo test --offline --locked --lib
families::timeseries` — the test suite was registered against a module stub
containing only module documentation, so every failure names API this packet
had not yet built (a compile-time RED; the tests exercise types and methods
that did not exist):

```text
error: could not compile `terrabase-v2` (lib test) due to 13 previous errors
 1 error[E0432]: unresolved imports `super::enforce_retention`, `super::rollup_series`, `super::run_series`
 1 error[E0432]: unresolved imports `crate::families::payloads::MAX_TIMESERIES_PAGE_LIMIT`, `crate::families::payloads::TimeSeriesPaging`
 1 error[E0599]: no method named `reading_rollup` found for struct `TimeSeriesRead`
 1 error[E0599]: no method named `with_paging` found for struct `TimeSeriesRead`
 3 error[E0599]: no method named `total` found for struct `TimeSeriesPage`
 6 error[E0599]: no method named `next_cursor` found for struct `TimeSeriesPage`
```

Covered tests (19): `rollup_windows_align_to_the_policy_window_and_stop_at_now`,
`rollup_windows_floor_negative_timestamps`,
`rollup_aggregations_match_the_policy_per_column`,
`rollup_rerun_without_new_points_stages_nothing`,
`rollup_rerun_after_new_points_extends_and_preserves_prior_windows`,
`rollup_policy_change_prunes_the_removed_section`,
`retention_drops_pre_horizon_entries_and_trims_rollups`,
`retention_keeps_points_whose_windows_are_incomplete`,
`retention_without_a_policy_is_a_no_op`,
`retention_skips_a_series_a_tombstones_evidence_window_still_covers`,
`retention_proceeds_once_the_evidence_window_no_longer_covers_the_document`,
`retention_evidence_for_another_document_or_tenant_does_not_bind_the_series`,
`retention_never_transitions_the_watermark`,
`run_tenant_drives_every_series_of_the_tenant`,
`run_series_rolls_up_before_retaining`,
`paging_limits_are_validated_at_the_seam_bounds`, `paging_chains_bounded_pages_to_exhaustion`,
`paging_cursors_are_stable_against_late_older_inserts`,
`paging_applies_to_rollup_reads_and_unknown_rollups_refuse`.

The tenant-wide drive (`run_tenant`) was added after the first commit-sized
implementation slice, with its own recorded RED — the module had
`run_series` only and the D1 text names the tenant drive as the tick unit:

```text
error[E0432]: unresolved import `super::run_tenant`
  --> src/families/timeseries/tests.rs:20:59
   | use super::{enforce_retention, rollup_series, run_series, run_tenant};
   |            no `run_tenant` in `families::timeseries`
```

### GREEN

After the implementation (`src/families/timeseries/` plus the payload, state,
store, and module-registration edits), the same command:

```text
$ env -u RUSTUP_TOOLCHAIN cargo test --offline --locked --lib families::timeseries
test result: ok. 19 passed; 0 failed; 0 ignored; 0 measured
```

All 19 tests pass, including the five that first failed against the
implementation with test-side expectation defects (three asserted stored
entries through the read seam, which hides pre-horizon points by its own
horizon rule — those were re-proven through follow-up runs that count the
still-stored entries; two were arithmetic mistakes in the expected windows:
the `[10,15)` window includes the raw points 10 and 11, and the original
series `0..10` spans ten points). The full receipts are in the packet
report; the dev-profile workspace suite is green (exit 0).

## Integrator rider (post-gate recorded limits)

Two warden observations are promoted from code comments into the recorded
limits so D3 carries them (gate findings):

- **Sequence-zero evidence is invisible to the retention skip.** The
  watermark read is strictly-greater (`deletion_evidence_above(tenant,
  StorageSequence::default())`); a hand-minted fact at sequence 0 would
  not bind. Engine-minted journal sequences are 1-based
  (`servable_history_start() == Some(StorageSequence::new(1))`), so no
  engine path can produce the exposed shape; the same strictly-greater
  bound is the watermark seam's own serving-query shape
  (`src/backup/smuggler/mod.rs`).
- **A regressed caller clock retracts complete rollup windows.** A
  `rollup_series` run with an earlier `now_ms` than a previous run
  recomputes and replaces sections to the earlier horizon
  (recompute-and-replace design; the caller-supplied-clock limit already
  applies — this names clock regression specifically).

Integrator receipts: both gates' focused suites green; full
`scripts/check.sh` on merged develop recorded at push time (the packet
worktree ran it green at `7509b1c`).
