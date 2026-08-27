# SE-06 repair receipt: socket-lane determinism (warden repair loop 1, MAJOR)

No production claim: this file records lane-scoped facts from the
engine-socket (QUIC) qualification lane of the SE-06 member-transport crate
only, reproducing the warden's flake condition on this host and recording
the fix and the streaks. Nothing here states a production topology, timing,
performance, or durability capability.

- Base: `develop @ ef72eca` plus the SE-06 packet commit `485dc9c`
  (branch `fleet/se06-2`, worktree `terrabasev2-se06`), crate
  `crates/terrabase-member-transport`
- Finding (warden, MAJOR): the engine-socket suite is wall-clock dependent
  and flaky under host load — sleep/timing-based delivery.

## The flake, reproduced (pre-fix, base `485dc9c`)

The pre-fix socket drain bounded every pump by a fixed wall-clock window
(`DEFAULT_PUMP_WINDOW`, 300 ms), ended its inbound loop as soon as one poll
came back quiet, and read uni streams only from connections accepted inside
the same pass. On an idle host the handshake, write, and read usually
landed inside the window, so the suite passed; under host load it did not.
Host load was `nproc` (28) busy-loop processes, one per logical CPU, started
before the run and stopped after it.

Run A — `cargo test --offline --locked --test quic_contract_scenarios`,
loaded: 17 passed, 5 failed
(`anti_entropy_repairs_a_lagging_member_after_a_gap`,
`delayed_and_reordered_replication_replays_idempotently_to_the_contract_outcome`,
`duplicate_delivery_applies_each_operation_once`,
`member_rejoin_checks_identity_and_catches_up_without_duplicates`,
`region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit`).

Run B — same command, loaded, minutes later: 20 passed, 2 failed
(`duplicate_delivery_applies_each_operation_once`:
"each operation applies once, left: 0, right: 2";
`the_socket_lane_restarts_and_rejoins_from_the_durable_cursor_without_intervention`:
`assert!(first.caught_up)` failed) — a different failure set from the same
command, which is the flake signature, not a deterministic defect.

Run C — the repair's red tests (`--test quic_lane_determinism`), loaded, on
the pre-fix lane: 2 of 4 failed
(`one_pump_completes_every_direction_of_a_fresh_lane_at_once`:
`m-c1 holds exactly the frames addressed to it, left [1, 2], right
[1, 1, 2, 2]`;
`fault_controls_decide_what_a_pump_delivers_not_the_clock`:
"the bounded drop loses exactly its count, left: 2, right: 1"). Idle, the
same four tests passed — the failure is a function of host load, which is
the finding.

## The fix (no wall-clock delivery semantics left in the lane)

1. A pump ends on a causal completion condition, never a duration. The
   drain keeps explicit progress accounting — frames taken from the
   outbound queues, frames written to sockets, frames still awaited — and
   returns once nothing written is still awaited. Arrival is attributed by
   peer endpoint address (each Member binds exactly one loopback endpoint,
   so the accepted connection's remote address names the sending Member).
2. Readiness is retained state: every accepted connection stays in a
   per-Member set across passes and pumps — a frame written on an existing
   connection is readable only while that connection's streams are polled —
   and leaves the set only when the peer closes it. This was the second
   half of the flake: the first causal version of this fix still lost
   frames under load until retained readiness was added (6 of 22 scenarios
   failed in the first loaded run after step 1 alone).
3. Socket futures are polled with a short bounded timeout (2 ms) so an
   idle pass returns to the loop that decides on causality; the timeout
   never ends a pump. A hang guard (30 s) bounds a pathological drain only
   — no healthy delivery has a size in it — and a guard expiry is counted
   as observable drops on the directions still waiting, never a silent
   loss.
4. The wall-clock knob left the public seam: `QuicLane::with_pump_window`
   and `DEFAULT_PUMP_WINDOW` are gone; delivery cannot be sized in
   milliseconds any more. A pump with nothing to deliver advances the lane
   clock and touches no socket.
5. Red-then-green regression tests: `tests/quic_lane_determinism.rs` (four
   tests) — one pump delivers every frame it accepted over a fresh
   connection (the first handshake, write, and read complete inside the
   pump, zero drops, nothing strays into a later pump); one pump completes
   every direction of a fresh lane at once; one pump delivers a full
   direction bound (`MAX_LINK_FRAMES`) in one step; and the W4 fault
   controls — partition, bounded drop, hold-back — decide what a pump
   delivers as tick facts, not the clock. The 22 contract scenarios are
   unchanged: same names, same scripts, same assertions.

No test is `#[ignore]`d: after the causal drain, no timing property in this
suite is inherently wall-clock.

## Streaks (post-fix, this host)

Command: `cargo test --offline --locked --test quic_contract_scenarios
--test quic_lane_determinism` from the crate root.

Idle (no artificial load), 5 consecutive runs — 5/5 green, 22 + 4 passed
each, 0 failed; contract-suite wall times 1.90 s, 2.52 s, 1.97 s, 2.12 s,
2.02 s.

Loaded (`nproc` = 28 busy-loop processes, one per logical CPU), 5
consecutive runs — 5/5 green, 22 + 4 passed each, 0 failed; contract-suite
wall times 2.57 s, 2.64 s, 2.49 s, 2.22 s, 2.35 s.

Loaded at twice the CPU count (56 busy loops), 1 run — green, 22 + 4
passed, 0 failed, 2.33 s.

Full crate suite (`cargo test --offline --locked`), idle — 73 passed, 0
failed (6 lib unit, 24 in-process contract, 9 envelope discipline, 8 NATS
usage, 22 engine-socket contract, 4 socket-lane determinism).

The recorded composite receipt
(`se06-lane-three-region-composite-engine-socket.md`) was re-produced after
the fix and is identical line for line — same fault list, acknowledgement
facts, convergence at lane tick 24, RPO 1 at window 3..=3, RTO 2 lane
ticks, convergence interval 8 lane ticks, lag distances, and per-class
bytes on wire — so that artifact stands unchanged; the fix changes when a
pump decides it is done, not what the script measures.

## Limits

Everything here is a fact of this host (28 logical CPUs) at these commits,
on loopback QUIC with fixture certificates from a lane certificate
authority that exists only for the run. The streaks prove the lane's
delivery causality under saturated-CPU load on this machine; they are not a
latency, throughput, or production-network measurement, and no number here
may be cited outside the lane.
