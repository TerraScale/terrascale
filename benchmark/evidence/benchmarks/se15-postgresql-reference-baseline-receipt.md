# SE-15 Benchmark Receipt: PostgreSQL reference baseline run slot

PostgreSQL-parity-or-better: inconclusive

verdict: inconclusive

This receipt records an **unrun** reference slot, not a measurement. No
PostgreSQL number exists in this repository and none is invented here.

## Run identity

- Ticket: TerraScale/TerraBaseV2#23 (SE-15), charter PD-2026-08-26-03 D2.
- Source of this packet's commit on branch `fleet/se15-2`; the adapter is
  `src/benchmarks/` and the pin record is
  `docs/benchmarks/se15-postgresql-reference-pin.md`.
- Launcher command a lane run will use (repository root, offline, locked):
  `cargo test --offline --locked --release --lib benchmarks -- --nocapture`
  for the TerraBaseV2 side, plus the operator-driven reference side against
  the pinned endpoint.

## Status: unrun, with the reason and the owner

- postgresql reference: unrun — no pinned PostgreSQL reference exists on
  this host: no `postgres`, `psql`, or `initdb` binary is installed and the
  `postgresql` service is inactive, and no `TERRABASE_PG_REFERENCE_ENDPOINT`
  is configured. The operator must provide the pinned PostgreSQL 18
  endpoint and capture its `SELECT version()` output.
  (owner: TerraScale benchmark lane owner (SE-15))

The environment gap was verified directly on this host before writing it
down: `which psql postgres pg_ctl initdb` resolves none of them on the
ambient `PATH`, and the host service state for `postgresql` is `inactive`.
The engine workspace also carries no PostgreSQL client — it has no external
dependency at all — so the reference side is driven by the operator, not by
this adapter.

## What is decided and what is not

Decided (in `docs/benchmarks/se15-postgresql-reference-pin.md` and
`src/benchmarks/reference.rs`, both test-pinned):

- PostgreSQL 18, the newest fully-supported stable major series.
- `synchronous_commit = on`, `fsync = on`, `full_page_writes = on`.
- Fresh empty data directory per run, same storage class as the engine
  location, discarded afterwards.
- Planner, memory, and every other setting at the pinned artifact's shipped
  defaults, recorded verbatim at the run.
- One table `users(tenant, id, name, "group", payload)` with a primary key
  on `(tenant, id)` and a btree index on `(tenant, name)`, created before
  warm-up.
- One prepared-statement connection per concurrency slot, autocommit per
  statement, 1,000 warm-up operations per workload excluded from every
  measured sample, and run-order alternation each sample.

Not decided by anyone, because only a running reference server can observe
them, and none has:

- the exact point release;
- the server's `SELECT version()` output;
- the pinned artifact's digest;
- the verbatim values of the defaults the pin leaves to the artifact.

The observation slot in `POSTGRESQL_REFERENCE.observed_identity` is `None`,
and the adapter's test
`the_postgresql_pin_records_a_decision_and_an_empty_observation_slot` pins
that it is `None`. It fills only when an operator captures the server's own
output at a lane run.

## Why this is not a hidden skip

- The adapter fails closed: `gates::reference_comparison_status` returns
  `(inconclusive, inconclusive)` for **any** comparison while either
  reference is `Unrun`, so no code path in the lane can produce `met` or a
  verdict from this state. The test `a_missing_reference_forces_inconclusive`
  pins that for all three missing-reference combinations.
- The run slot is a named deliverable with a named owner, recorded in the
  pin document and in the adapter's `REFERENCE_OWNER` constant.
- The workload mapping, the fairness assumptions, the statistical gate, the
  warm-up discipline, the recorded-run minimums, and the TerraBaseV2 side
  of the lane are all implemented, tested, and run for real
  (`se15-v2-lane-commissioning-receipt.md`). What is missing is exactly the
  one thing this host cannot supply: a PostgreSQL server.

## Validity notes

- This receipt makes no performance statement of any kind. There is no
  TerraBaseV2-versus-PostgreSQL number to state.
- The objective status is `inconclusive` — an honest outcome under
  PD-2026-08-26-03 D1, not a failure to fill in a blank.
- No durability setting on either side was changed to prepare for the
  future run.
