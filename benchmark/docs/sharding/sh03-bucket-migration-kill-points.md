# SH-03 kill-point evidence: live bucket migration

Status: focused-run receipt for SH-03 (`TerraScale/TerraBaseV2#49`,
PB-5038/5039, ADR 0011 D3). Lane statement: the kill-point tests run
**in-process** against real durable engines (temporary directories, real
publication journals and operation ledgers); the **exclusive recovery lane is
reserved for BK-02** and no recovery-lane claim is made here. No sharded
performance claim is made or implied anywhere in this record.

## Scope

`src/sharding/migration.rs` (the bucket-migration module family),
`src/sharding/migration_tests.rs` (in-crate contract lane),
`tests/sharding_migration/` (public-surface integration tests). The
replication plane (`src/replication/**`) is untouched: migration streams a
bucket's committed records by composing the same machinery the member
catch-up is built from — bounded journal pages, gap fallback, and
idempotent apply under original operation identity through the engine
transaction seam — with the bucket filter at the migration layer, because
the member catch-up serves whole tenants and has no bucket-scoped serving
path (recorded as a collision action for the integrator).

Repair-loop-1 note (warden findings on the first packet): the fail-closed
divergence rule required exposing the engine's journal divergence as a
count. Two additive edits outside this module family — the
`StoragePort::journal_divergence` seam plus its `FileStorage` derivation
(`src/storage/mod.rs`, `src/storage/file.rs`) and the
`Engine::journal_divergence` mirror (`src/engine/mod.rs`) — are declared
for the integrator's ratification. Both are read-only derivations over
existing durable state; no storage format, write path, or public type
changed.

## Contract the tests state

- V1's observable migration status shape is kept: pending -> moving ->
  moved -> ownership transferred, plus clean rollback.
- Streaming resolves only the migrating bucket's committed records at the
  destination; an atomic record spanning buckets applies atomically; a
  record whose bucket cannot be resolved (a delete under a field shard key)
  is streamed conservatively.
- No double-apply (PB-5039): re-scanned records resolve as duplicates
  through the durable operation ledger; an operation identity bound to a
  different write set refuses with the typed `OperationIdReuse`.
- The cutover seats exactly the bucket-move table edit with raised epochs
  (SH-02's `with_bucket_moved` + `ShardTopology::transition`), idempotently.
  A migration that would drain the source shard out of the table seats only
  against a Platform view that drops the source shard (the seating rules
  give every owned range buckets) — the draining-cutover test states this
  boundary.
- The in-flight window is bounded and bucket-scoped: one round scans at
  most `MAX_MIGRATION_STREAM_BATCH` (1024, the mirrored replication
  catch-up bound) journal entries, and the cutover refuses any bucket
  record past the confirmed point — or a bucket-bearing delta wider than
  one bounded page — returning the migration to moving. Co-tenant journal
  width is never a refusal: the documented Platform boundary stops writes
  to the migrating bucket only (PB-5039), so co-tenant width past the
  confirmed point pages through as bounded scan cost and cannot starve a
  quiesced bucket's cutover.
- Both completeness proofs fail closed on a diverged journal. The engine's
  journal append is deliberately best-effort (W4-CA-4 repair F2), so a
  commit can be published without being journaled, and a journal-only
  proof is blind to exactly that record. The engine exposes the
  divergence as a derived count (`Engine::journal_divergence`: ledger
  entries resolved committed whose sequence the journal does not serve —
  additive seams in `src/storage` and `src/engine`, declared for the
  integrator), and a nonzero count refuses confirmation and cutover with
  the typed `JournalDivergence`, never a silent strand. The count is
  derived from durable state, so it holds across appends that heal the
  high-water mark and across reopens; an aged journal (a head the
  servable bound no longer covers) reports the same refusal.

## Kill-point matrix (in-process interruptions)

| Kill point | Test | Outcome asserted |
| --- | --- | --- |
| After start, before any streaming | `kill_points_recover_to_completion_or_clean_rollback` (K1) | resume + full drive completes; bucket routes to destination |
| Mid-streaming | `streaming_is_restartable_without_double_apply` | engines dropped and reopened from their durable directories; the re-drive applies nothing fresh, resolves every record as a duplicate, and loses no record |
| Mid-streaming, then abandoned | `kill_points_...` (K2) | rollback leaves ownership unchanged; a later fresh migration completes without double-apply |
| After confirmation, before cutover | `kill_points_...` (K3) | resume re-confirms and cuts over |
| After the cutover seat | `kill_points_...` (K4) and `cutover_is_idempotent_across_an_interrupted_seat` | resume derives the transferred state from routing facts; re-issued cutover replays idempotently |
| New records in flight past confirmation | `cutover_refuses_records_in_flight_past_the_confirmed_point` | cutover refuses (`InFlightDelta`), migration returns to moving, delta drains, cutover then seats |
| Diverged journal at confirmation | `confirmation_refuses_a_diverged_publication_journal` | an armed `JournalAppend` failure publishes a bucket record the journal never records; the confirmation refuses `JournalDivergence` and stays moving instead of declaring completeness it cannot prove |
| Diverged journal at cutover | `cutover_refuses_a_diverged_publication_journal` | the same divergence past the confirmed point refuses the cutover (`JournalDivergence`); the seat never strands the unjournaled record on the source |
| Co-tenant width past confirmation | `co_tenant_width_does_not_starve_a_quiesced_buckets_cutover` | 1025 post-confirm entries of a non-migrating tenant (plus a same-tenant out-of-bucket record) page through as scan cost; the cutover seats while only the migrating bucket is quiesced |
| Bucket-bearing width past confirmation | `a_bucket_bearing_delta_wider_than_one_page_refuses_and_returns_to_moving` | 1025 unstreamed bucket records refuse `InFlightDelta` naming the full page's last sequence; the migration returns to moving |
| Aged journal below the cursor | `an_aged_journal_falls_back_to_a_full_rescan_of_duplicates` | with the journal's first record aged away, the resumed round reports `fell_back_to_full_scan`, re-scans resolve as ledger duplicates, and the confirmation fails closed on the aged divergence |
| Draining cutover (source shard's last bucket) | `a_draining_cutover_needs_a_view_that_drops_the_source_shard` | a next view still owning the drained source refuses `OwnedRangeWithoutBuckets`; the Platform's draining view (source dropped, destination raised) seats |

## In-flight queries across cutover

`in_flight_queries_resolve_correctly_across_cutover`: a placement routed
before the cutover for the migrated bucket is fenced by the typed
`PlacementStale` refusal on the transferred topology, re-routing resolves
to the destination owner at the raised epoch, and the fresh placement
authorizes. A placement for an untouched bucket of the destination shard is
fenced by `PlacementEpochSuperseded` — the epoch raise fences every
placement of an affected shard, never just the migrated bucket's — and
re-routing resolves to the same owner at the new epoch.

## Focused runs

Commands run from the worktree root (offline, locked):

- `cargo test --offline --locked --lib sharding::migration` —
  19 tests, all pass.
- `cargo test --offline --locked --test sharding_migration` —
  5 tests, all pass.
- `cargo fmt` and `cargo fmt --check` — clean.
- `cargo check --offline --locked` — pass.
- `cargo clippy --offline --locked --all-targets -- -D warnings` — pass.
- `cargo test --offline --locked` — 286 lib tests + 56 integration tests
  across all 12 green targets, 0 failures.

## Limits

- Deterministic in-process runs only: the interruptions are step boundaries
  of the typed operations, not injected process kills; crash-restart
  qualification beyond the engine's own recovery contract belongs to the
  exclusive recovery lane (BK-02).
- The Platform's side of the coordination boundary (stopping writes to the
  migrating bucket for the final cutover, scheduling, placement) is assumed,
  per PB-5039 and ADR 0011 D3: the engine validates and executes typed
  operations; the cutover's delta re-check is what bounds the window the
  engine owns.
- Per-shard replication receipts, sharded fan-out (SH-04), and sharded
  failover (SH-05) are not claimed here.
