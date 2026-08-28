# DB-07 topology demo receipt: synthetic-lane population of the topology view

No production claim: every fact below is a studio-fixture demo fact from the
deterministic synthetic lane (ADR-0004 D-4). This receipt is secret-safe: it
carries identities, counts, tick numbers, and verdicts only — no credentials,
document bodies, connection strings, or private host paths.

- Ticket: TerraBase/TerraBaseV2#19 (DB-07), branch `fleet/db07-2`
- View: `studio/app/topology/page.tsx` (route `/topology`)
- Demo module: `studio/app/topology/fixtures/topology-demo.mjs`
- Fixture routes: `POST /api/v2/operations/topology.read`,
  `POST /api/v2/operations/topology.recent-writes`,
  `POST /api/v2/operations/topology.convergence` (DB-07's route namespace in
  the shared dev fixture; no other fixture route changed)

## Scenario

The qualification topology the replication seam declares (three synthetic
Regions `r-alpha`, `r-beta`, `r-gamma`; six synthetic Members `m-a1`..`m-g2`;
each Region one Failure Domain) with the fixed [0, 2^20) bucket space
partitioned into three Bucket Ranges (`shard/1`, `shard/2`, `shard/3`), each
owned by one Member at its ownership epoch (1, 2, 3). The lane clock advances
five ticks per topology read; wall time never enters the model, and every
fact is a pure function of the lane tick.

## Facts asserted (producing tests)

- Unit (`node --test tests/topology-fixture.test.mjs`, 8 tests): envelope
  discipline on the topology namespace (405/400/401/404/503 fail-closed
  paths, correlation echo), snapshot shape (Regions in canonical order,
  Failure Domains never spanning Regions, Bucket Range table partitioning
  [0, 2^20) exactly with adjacent-same-shard entries merged, epochs >= 1),
  AcknowledgementReport facts (all four levels exercised; op-4 requests
  all-regions and observes region-quorum while a Region lacks quorum and one
  Member serves degraded), Convergence (null before the first observation,
  `op-4` observed at lane tick 108 at steady state, six holdings each at
  Local), lag arithmetic (distance = source high water − Member high water,
  zero at steady state), scheduled health transitions, and the absence of
  control-plane fields in every payload.
- Unit (`node --test tests/topology-vocabulary.test.mjs`, 7 tests): the wire
  enums and the convergence/lag measurement definitions are extracted from
  `src/replication/mod.rs` and match the demo module verbatim; the subtree
  copy uses the CONTEXT.md terms and avoids the avoided synonyms; the nav
  links `/topology`.
- E2E (`vp run playwright:e2e`, 5 topology specs, 14 total passing): the
  route renders through typed reads with a browser making only
  `topology.read` polls; a scheduled member status transition (m-b2
  Responsive → Degraded at lane tick 70) renders through the polled fallback
  within two polls; typed failures render the error route and recover; the
  loading route streams during in-flight reads.

## Measurements

Lane-scoped only: lane ticks (observation identities), storage-sequence
distances (lag), and counts (Members, Regions, writes, ranges). RPO and RTO
are not measured by this demo and are not displayed. No latency, throughput,
or production claim is made or implied anywhere on the route.

## Limits

- Synthetic lane (ADR-0004 D-4): fabricated demo observations; no production
  claim.
- The topology reads are fixture-served pre-SE-08 shapes
  (`studio/app/topology/fixtures/topology-contract.fragment.json` holds the
  merge-ready contract fragment); the integrator folds them into
  `studio/contract/terrabase-api.contract.json` and the generated typed
  client.
- Member status is a health observation (ReplicationStatus); it never
  carries or implies a Convergence fact.
- Acknowledgement Level is part of the public contract and is distinct from
  local receipt.
