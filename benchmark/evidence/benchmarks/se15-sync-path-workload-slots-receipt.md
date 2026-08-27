# SE-15 Benchmark Receipt: sync-path workload class slots

PostgreSQL-parity-or-better: inconclusive

verdict: inconclusive

This receipt records the sync-path workload class the ticket mandates
beside the CRUD set (PD-2026-08-26-05 D4, PD-2026-08-26-13 D1):
commit-to-delivery latency, delivery throughput under load, and bandwidth
— bytes-on-wire per delivered change and trigger-notification size — on
the client and member links. Each workload is **mapped and declared, not
driven**: this is the receipt of open slots with named owners, not of a
measurement. No sync-path performance number is claimed here, and no
deterministic counter from another receipt is promoted into one.

## Run identity

- Ticket: TerraScale/TerraBaseV2#23 (SE-15), acceptance criterion 5.
- Source: this packet's commits on branch `fleet/se15-2`. The declaration
  is data in the adapter — `src/benchmarks/lane.rs`
  (`Workload::SYNC_PATH`, part of `Workload::ALL`, with
  `Workload::driver_status` naming each slot's reason and owner) — pinned
  by the test `the_sync_path_class_declares_its_open_adapter_slots`.
- Mapping and fairness: `docs/benchmarks/se15-workload-mapping.md`, the
  sync-path class section (W-6..W-9), including the per-workload
  no-PostgreSQL-equivalent statement.
- The comparison this class owes runs against the TerraBase V1 baseline
  and the written budgets (ADR-0008 D-4; PD-2026-08-26-13 D1), not
  against PostgreSQL — the class fairness statement. Both reference slots
  are nonetheless recorded honestly:
  - postgresql reference: unrun — no pinned PostgreSQL reference exists on
    this host (see `se15-postgresql-reference-baseline-receipt.md`).
    (owner: TerraScale benchmark lane owner (SE-15))
  - terrabase v1 reference: unrun — the pinned V1 checkout is read-only
    here and its harness needs a build plus containers this offline
    environment does not provide (see
    `se15-v1-reference-baseline-receipt.md`).
    (owner: TerraScale benchmark lane owner (SE-15))

## Status per workload: unrun, with the reason and the owner

- `sync-commit-to-delivery-latency` — **unrun.** Links: client (commit
  publication, content-free trigger, checkpoint-bounded delta pull through
  the C# host/TBEP plane) and member (push/catch-up delivery to a member's
  apply). Reason: a wall-clock quantity needs the served sync plane beside
  the engine and the SE-15 statistical gate with the V1 baseline run; this
  in-process lane drives the engine core only, and the sync plane lives in
  the sibling crates (`terrabase-sync-core`, `terrabase-member-transport`,
  the TBEP host) that the zero-dependency engine crate cannot call.
  (owner: SE-15 follow-up owner: the benchmark lane owner, with the SE-07
  sync-core and member-transport lane owners)

- `sync-delivery-throughput` — **unrun.** Links: client (delta batches
  served per budget window) and member (changes delivered per catch-up
  round while a backlog drains). Reason: same as the latency slot —
  wall-clock under load needs the served plane and the gate with the V1
  baseline run. The deterministic bounds that exist (SE-07 E-1 catch-up
  rounds within `ceil(B / batch) + 1`; E-8 delta-batch utilization at or
  above the written 70%) are CI-pinned floors, not rates, and are not
  presented as throughput.
  (owner: same as the latency slot)

- `sync-bytes-on-wire` — **unrun (the comparison run).** Links: client
  (delta-batch/snapshot wire bytes per delivered change, held against the
  written 65,536-byte delivery budget and 38-byte fixed envelope) and
  member (per-direction envelope byte counters over push, catch-up, and
  acknowledgement frames, held against the written 512-byte per-change
  budget). The deterministic TerraBaseV2-side counters already exist and
  are receipted — SE-07 E-11 recorded 25 wire bytes per delivered change
  on the client-link fixture workload, and the SE-06 composite-lane
  receipts recorded the member link within its per-change budget on both
  lanes. What has not run is the comparison alongside the V1 baseline
  under the SE-15 gate with this packet's fairness documentation; the lane
  adapters that own those counters live in the sibling crates, and
  re-deriving them inside the engine crate would duplicate them rather
  than measure them.
  (owner: SE-15 follow-up owner: the benchmark lane owner, with the SE-07
  sync-core and member-transport lane owners)

- `sync-trigger-notification-size` — **unrun (the comparison run).** Link:
  client only — the encoded content-free `trigger` frame of
  `terrascale.sync.v2` (collection scope, storage sequence, operation id,
  nothing else). The member link carries no trigger-notification frame
  class by design (members are pushed the data envelopes themselves; that
  size is the `sync-bytes-on-wire` slot), so there is no member-link
  measurement to owe here. The deterministic TerraBaseV2-side measurement
  exists and is pinned — 77 bytes in the recorded SE-04 vector, within the
  256-byte written budget, asserted by SE-07 E-11 and replayed byte-exact
  by the golden corpus. What has not run is the receipted comparison
  alongside the baselines under the SE-15 gate, whose lane adapter lives
  in the sync-core crate the engine crate cannot call.
  (owner: SE-15 follow-up owner: the benchmark lane owner, with the SE-07
  sync-core and member-transport lane owners)

## Why this is not a hidden skip

- The declaration is data in the adapter, not prose: `Workload::SYNC_PATH`
  is part of `Workload::ALL`, every workload names both links it measures
  and its comparand statement, and the test
  `the_sync_path_class_declares_its_open_adapter_slots` fails if any of
  that regresses.
- The lane driver refuses rather than skipping: `V2Lane::run` panics for
  any workload with an open slot (the session workload's rule, unchanged),
  so a sync-path workload cannot be quietly no-op'd into a receipt.
- No fabricated substitute: the deterministic neighbors cited above are
  byte counts, frame counts, sequence distances, and lane ticks — each
  labelled as what it is. None is a latency or a rate, and none is
  presented as one.
- The fairness statement is explicit per workload: PostgreSQL is not the
  comparand for this class (`LISTEN/NOTIFY` and WAL replication are
  different contracts with no shared budget); the comparison set is V2
  against its written budgets and the V1 baseline, under the same gate,
  warm-up, and alternation discipline as the CRUD workloads.

## What is owed next

- The served-lane run for the two wall-clock slots
  (`sync-commit-to-delivery-latency`, `sync-delivery-throughput`): the
  served sync plane beside the engine (C# host/TBEP client link,
  member-transport lane) driven under the SE-15 gate with the V1 baseline.
- The comparison run for the two bandwidth slots (`sync-bytes-on-wire`,
  `sync-trigger-notification-size`): the existing crate-side deterministic
  counters re-run beside the V1 baseline under the SE-15 gate and
  receipted against the written budgets with this mapping's fairness
  documentation.
- Coordination: the lane owner with the SE-07 sync-core and
  member-transport lane owners (the adapters that already pin the
  deterministic quantities), plus the SE-08 host surface for the served
  client link.

## Validity notes

- No performance statement of any kind is made here. The deterministic
  values quoted from the SE-07/SE-06 receipts are byte and frame counts
  with their own provenance; quoting them is not measuring them.
- The objective status is `inconclusive` — the honest outcome while the
  class's comparison has not run. PostgreSQL-parity is not the question
  this class asks (there is no PostgreSQL comparand); the status line is
  carried anyway because the standing discipline requires exactly one per
  receipt, and `inconclusive` states the truth: nothing here answers any
  objective.
