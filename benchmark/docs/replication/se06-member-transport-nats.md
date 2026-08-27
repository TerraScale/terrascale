# SE-06 implementation record: member transport on engine-owned lanes; NATS as KV/config/discovery only

Status: implementation record for ticket SE-06 (TerraBaseV2 issue #8),
2026-08-26. Authority: charter PD-2026-08-26-13 D2 (parent record ADR
0012), the Orchestrator NATS spec's separation rules
(`specs/orchestrator-nats-native-control-plane.md`), and the accepted W4
replication plane (`docs/replication/w4-replication-r1..r3.md`,
ADR 0004 D-1..D-4). Worktree `terrabasev2-se06`, branch `fleet/se06-2`,
based on `develop @ ef72eca`. This record resolves no parity row and makes
no capability claim: every measurement quoted below is a lane-scoped fact
from the receipts under `evidence/replication/`.

## What was built

One new standalone-workspace crate, `crates/terrabase-member-transport`
(own `[workspace]` root, exactly as `crates/terrabase-sync-core` shipped
before its integrator wiring — the root `Cargo.toml` members-list edit is
the recorded integrator action, see "Integrator wiring notes"):

- `src/envelope.rs` — the versioned member envelope and its fail-closed
  wire codec: schema name and major/minor version, message/correlation/
  causation identities, idempotency keys where mutation is possible, source
  and target Members, issued/expiry lane ticks, range+epoch whenever the
  frame can mutate data-range state, and a SHA-256 payload digest
  (Orchestrator NATS spec "Message contract" discipline, applied to the
  member plane). Unknown schema/version/kind, truncation, trailing bytes,
  length lies, invalid UTF-8, oversized fields/frames, digest
  disagreement, wrong target, and expiry all fail closed with typed
  errors. Carries the two written PD-13 D1 budget constants (provisional
  until SE-07 ratifies them).
- `src/transport.rs` — the `MemberTransport` seam: per-direction W4 fault
  controls (delay, reorder, bounded drop, duplicate, full partition) with
  observable counters, and per-frame-class bytes-on-wire accounting at the
  delivery boundary. The lane clock (one tick per pump) lives here.
- `src/in_process.rs` — the in-process lane: bounded encoded-frame queues
  between Members sharing a process boundary; fully deterministic (no
  threads, no async, no sleeps, no wall time).
- `src/quic.rs` — the engine-socket lane (`quinn` QUIC, `rustls` ring
  provider, `rcgen` lane CA): one endpoint per Member on loopback, one
  certificate per Member (no shared Member credentials), mutual TLS, one
  uni stream per frame. Pumps flush outbound and drive inbound
  concurrently and end on a causal completion condition, not a clock: the
  peer's `accept` must be polled for a connecting handshake to complete —
  the flush and the inbound driver run joined — and a pump returns only
  once every frame it wrote to a socket has been read off the wire by the
  addressed Member (explicit drain accounting: frames taken, written, and
  still awaited, with arrival attributed by peer endpoint address). The
  inbound driver keeps every accepted connection as readiness state across
  passes and pumps, because a frame written on an existing connection is
  readable only while that connection's streams are polled. Socket futures
  are polled with short bounded timeouts so an idle pass returns to the
  loop that decides on causality; a generous hang guard bounds a
  pathological drain only, and its expiry is counted as observable drops,
  never a silent loss. Tick-valued protocol vocabulary throughout;
  receipts record measured ticks as run facts.
- `src/journal.rs` — the lane's durable per-Member publication journal
  (magic+version header, SHA-256 per record, fsync per commit): global
  sequence, per-tenant sequences, servable-history bound, the durable
  operation ledger that decides duplicates and rejects identity reuse with
  different write sets, and journal-derived cursors that survive restart.
  Fail-closed open: a location holding anything that does not verify fails
  with the typed `FormatMismatch`.
- `src/member.rs` — the W4 member semantics re-run over the transports:
  D-1 admission fencing + apply-time epoch fencing + deposed-owner
  retention, D-2 acknowledgement lattice with the ack observation crossing
  the wire (every received push answered by an ack-report envelope after
  its apply pass), D-3 bounded-HLC last-writer-wins with content-free
  conflict history, tenant-isolation denials at both seams, catch-up gap
  fallbacks, rejoin cursor validation, degenerate-lattice normalization.
- `src/lane.rs` — the three-Region qualification harness (`r-alpha`,
  `r-beta`, `r-gamma`; two Members per Region; one failure domain per
  Region; six durable journals) over any transport, plus the fixed
  composite scenario script and the bytes-on-wire accounting against the
  PD-13 D1 budgets.
- `src/receipt.rs` — the lane-scoped, secret-safe `LaneReceipt` artifact
  (identities, counts, sequence windows, tick counts, byte counts,
  definitions, limits; no document content, credential, connection string,
  or host path).
- `src/nats_usage.rs` — the NATS KV/config/discovery usage boundary: the
  three allowed roles under `terrabase.db.<cluster>.{kv,config,discovery}`
  subjects (NATS-token-validated, stricter than identifiers), a closed
  record set with no byte and no free-text field anywhere, revisioned KV
  puts with compare-and-swap ownership transitions, monotonic versioned
  config, heartbeated discovery with a lease-bounded
  healthy/stale/missing observation, a two-account fixture with disjoint
  generated credentials and enforced namespace separation, and the
  explicit `audit()` proving no database data rides NATS.

Dependencies: `quinn`, `rustls` (ring), `rcgen`, `tokio`, `ring` — the
engine-socket closure only; the crate adds nothing else.

## The 23 W4 contract scenarios re-run over engine-owned transports

The W4 red-test names (`docs/replication/w4-replication-r3.md`) re-run as
public-seam tests of the new crate — identical scripts, identical outcome
assertions — over both lanes:

| # | Test name | In-process | Engine-socket |
| --- | --- | --- | --- |
| 1 | `local_commit_receipt_contains_no_region_or_member_facts` | green | type-level (no transport beneath it) |
| 2 | `replication_request_names_an_explicit_acknowledgement_level` | green | type-level |
| 3 | `acknowledgement_report_names_the_regions_and_members_that_contributed` | green | green |
| 4 | `acknowledgement_timeout_is_distinct_from_local_commit_failure` | green | green |
| 5 | `single_region_single_member_write_reads_back_and_survives_restart` | green | green |
| 6 | `one_region_multiple_members_reach_region_quorum_under_member_delay` | green | green |
| 7 | `two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility` | green | green |
| 8 | `member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success` | green | green |
| 9 | `region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit` | green | green |
| 10 | `delayed_and_reordered_replication_replays_idempotently_to_the_contract_outcome` | green | green |
| 11 | `duplicate_delivery_applies_each_operation_once` | green | green |
| 12 | `member_rejoin_checks_identity_and_catches_up_without_duplicates` | green | green |
| 13 | `restarted_member_resumes_from_its_durable_replication_cursor` | green | green |
| 14 | `anti_entropy_repairs_a_lagging_member_after_a_gap` | green | green |
| 15 | `convergence_report_names_members_operation_identity_and_observation_time` | green | green |
| 16 | `replication_status_reports_health_separately_from_convergence` | green | type-level |
| 17 | `isolated_concurrent_writes_converge_deterministically_after_healing` | green (+ determinism double-run) | green (outcome assertions) |
| 18 | `conflicted_writes_produce_the_recorded_policy_outcome_and_a_content_free_conflict_record` | green | green |
| 19 | `leader_loss_stops_new_commits_until_a_new_leader_is_confirmed` | green | green |
| 20 | `old_owner_is_fenced_after_leader_transition` | green | green |
| 21 | `tenant_isolation_probe_denies_cross_tenant_replication_and_is_observable` | green | green |
| 22 | `replication_receipt_is_secret_safe_and_names_scenario_faults_and_limits` | green | green |
| 23 | `three_region_fault_lane_reports_measured_rpo_rto_convergence_and_lag` | green | green |

No listed name was weakened, renamed, or dropped. Names 1, 2, and 16 are
type-level vocabulary tests with no transport beneath them; 17's
double-run determinism proof is an in-process property by construction
(the socket lane's delivery timing is the network's), so over sockets the
scenario asserts its converged outcome and the recorded conflict decisions.
Focused extras beyond the list: the codec's adversarial fail-closed suite
(`tests/envelope_discipline.rs`, nine tests including wrong-target and
expiry at the member boundary and the measured-overhead budget check), the
in-process composite determinism double-run, the socket lane's
restart-and-rejoin catch-up from the durable cursor without operator
intervention, the socket lane's bytes-on-wire budget check, the socket
lane's pump-causality suite (`tests/quic_lane_determinism.rs`, four tests:
one pump is a complete delivery step over a fresh connection, across every
direction of a fresh lane, and at a full direction bound, and the fault
controls — not the clock — decide what a pump delivers), and the eight
red-then-green NATS usage tests.

## The 11 fault classes over real links

The composite scenario's `faults:` line names the full W4 vocabulary —
`link-delay`, `link-reorder`, `link-drop`, `link-duplicate`,
`region-partition`, `member-restart`, `member-rejoin`, `leader-loss`,
`region-loss-after-acknowledged-write`, `healing`, `duplicate-replay` — and
the fault controls fire on real frames: over bounded encoded queues
(in-process) and over real QUIC sockets with observable per-direction
counters proving each fault actually fired (held, reordered, dropped,
duplicated, partitioned).

## NATS is KV, config sharing, and discoverability only

PD-2026-08-26-13 D2 in code: subjects exist only in the three role shapes
under the database's own `terrabase.db.` namespace (anything else fails
closed at construction, including the control plane's
`terrabase.orchestrator.` namespace); the record set is closed
(identifiers, numbers, addresses — no byte, no free text); ownership
transitions go through compare-and-swap with epoch fencing; config
versions are monotonic; discovery heartbeats carry a lease-bounded
healthy/stale/missing observation; and `audit()` walks the store proving
every subject, every value kind, every field, and the account separation —
rendered as the artifact in `evidence/replication/se06-nats-usage-audit.md`.
Cluster separation is proven in the fixture: two accounts, disjoint
generated credentials, and typed `CrossAccountSubject` / `BadCredentials`
rejections in both directions.

## Receipt inventory (new, under `evidence/replication/`)

1. `se06-lane-three-region-composite-in-process.md` — the composite over
   the in-process lane, with the bytes-on-wire accounting and both PD-13
   D1 budget checks.
2. `se06-lane-three-region-composite-engine-socket.md` — the same script
   over real QUIC sockets, with the socket lane's four-byte-wire-framing
   limit recorded.
3. `se06-nats-usage-audit.md` — the NATS boundary audit artifact and its
   unrun note (no live `nats-server` in this environment).
4. `se06-quic-lane-determinism.md` — the warden-repair receipt: the
   pre-fix flake under host load, the causal-drain fix, and the 5-run
   streaks idle and loaded.

## Salvage note (predecessor partial work)

A previous builder on this branch left the crate skeleton and most module
bodies uncommitted and never compiled: the test target did not build (a
`Result<MemberJournal, _>` equality assert), the crate had never run a
test, and the socket lane had never delivered a frame (its drain flushed
outbound before anyone drove the peers' `accept`, so every first-connection
send stalled past the handshake timeout and dropped its frames silently).
This packet adopted the correct parts (envelope, identity, journal,
in-process lane, member semantics, receipt — all verified by the ported
scenario suite before anything else was written), fixed the build, and
redesigned the socket drain (joined flush + inbound driver, bounded
timeouts, observable drop accounting). Everything was re-verified from
zero: the full suite, clippy with warnings denied, and rustfmt were run by
this packet only. The NATS module and every test file are new work,
test-first (the NATS tests were confirmed red against the absent module
before implementation).

## Integrator wiring notes (collision actions)

1. Root `Cargo.toml`: add `crates/terrabase-member-transport` to the
   workspace `members` list (the crate ships with its own `[workspace]`
   root and `Cargo.lock` so the engine workspace stays untouched until
   then — same pattern `terrabase-sync-core` shipped under). Unify the
   lock afterwards; the crate's closure is quinn/rustls(rcgen)/tokio/ring.
2. The lane journal is the durable stand-in for the engine's publication
   journal; wiring real Engines behind `LaneMember` (the acquisition seam)
   replaces `src/journal.rs`'s role in production.
3. `scripts/check.sh` / CI: once the crate joins the workspace, the
   repository-wide gates pick it up automatically (fmt, clippy -D
   warnings, tests, docs all pass standalone in this packet; the
   socket-lane tests bind loopback QUIC endpoints only).
4. Charter/AGENTS wording already states the NATS role; no doc outside
   this record and the evidence files was touched.

## Warden repair loop 1: socket-lane determinism (MAJOR)

Finding: the engine-socket suite was wall-clock dependent — each pump
drained socket traffic inside a fixed wall-clock window, ended its inbound
loop as soon as a poll came back quiet, and read streams only from
connections accepted inside the same pass — so under host load the same
script delivered different frames (five of 22 contract scenarios failed in
one loaded run, two in another; the receipt under
`evidence/replication/se06-quic-lane-determinism.md` records both). The
fix removes the wall-clock dependence rather than widening it:

1. A pump ends on a causal completion condition, never a duration: the
   drain keeps explicit progress accounting (frames taken from the
   outbound queues, frames written to sockets, frames still awaited) and
   returns once nothing written is still awaited, with arrival attributed
   by peer endpoint address. The wall-clock pump window
   (`QuicLane::with_pump_window`, `DEFAULT_PUMP_WINDOW`) is gone from the
   public seam — delivery has no size in milliseconds any more.
2. Readiness is retained state, not a timing coincidence: every accepted
   connection stays in a per-Member set across passes and pumps (a frame
   written on an existing connection is readable only while that
   connection's streams are polled) and leaves the set only when the peer
   closes it.
3. Socket futures are still polled with a short bounded timeout, but the
   timeout only returns an idle pass to the loop that decides on causality;
   a generous hang guard bounds a pathological drain only and its expiry is
   accounted as observable drops on the directions still waiting.
4. Red-then-green: `tests/quic_lane_determinism.rs` pins the contract
   (one pump is a complete delivery step over a fresh connection, across
   every direction of a fresh lane, and at a full direction bound, with
   zero drops on a healthy lane; the W4 fault controls — partition, bounded
   drop, hold-back — decide what a pump delivers, as tick facts). It was
   red under host load on the pre-fix lane and is green on the fixed lane
   idle and loaded.
5. A pump with nothing to deliver advances the clock and touches no socket
   (previously every pump paid the window even when idle), which is also
   why the socket suite runs faster after the fix.

No assertion in the 22 contract scenarios was weakened, renamed, or
dropped; no test is `#[ignore]`d, because after the causal drain no timing
property in this suite is inherently wall-clock.

## Checks (this packet, from the crate root)
- `cargo fmt --all -- --check` — pass
- `cargo test --offline --locked` — 73 passed, 0 failed (6 lib unit, 24
  in-process contract, 9 envelope discipline, 8 NATS usage, 22
  engine-socket contract, 4 socket-lane determinism)
- `cargo +1.97.1 clippy --offline --locked --all-targets --all-features -- -D warnings` — pass
- `cargo doc --offline --locked --no-deps` — pass (missing_docs denied at
  compile)
- Socket-lane streaks (the warden's acceptance condition): the
  engine-socket contract suite passed 5 consecutive runs idle and 5
  consecutive runs under full-CPU host load, and the determinism suite
  passed the same 5+5 — receipts in
  `evidence/replication/se06-quic-lane-determinism.md`.
- The engine workspace at the repository root is untouched by this packet
  (the crate is a standalone workspace root), so `scripts/check.sh`'s
  repository gates are unaffected until the integrator wiring above.

## Non-claims

Everything measured here is a lane-scoped fact: no production topology,
timing, bandwidth, or durability capability is implied; the PD-13 D1
budgets stay provisional until SE-07 ratifies them with receipts; the NATS
audit is against the fixture's semantics, with the live-server run
recorded as unrun.
