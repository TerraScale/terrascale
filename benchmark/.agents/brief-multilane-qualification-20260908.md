# Multi lane SQLite qualification setup

Source audit: 2026-09-08. This is an implementation handoff/evidence note;
the product and benchmark sources remain unchanged.

## Finding

The safe provisioning primitive already exists in the engine. A target must be
created through `ScopedShardEngine::create_split_target` in
`src/engine/shard_split.rs:289-325`, which calls
`Engine::open_shard_participant_mode(..., create=true, lane_incarnation=...)`
(`:943-1007`). That path charges the owning `Member` resource factory before
SQLite opens, persists a distinct `FileShardIdentity`, copies schema metadata,
and installs the target's lane origin. Opening a hand chosen SQLite path or
constructing a second `Engine` bypasses identity, authority, encryption, and
resident budget checks and is unsuitable for qualification.

The complete one split sequence is executable today in the
`admitted_target` fixture at `src/engine/shard_serving_correction_tests.rs:14-75`:

1. open a durable source through the owning Member/test helper;
2. install the generation-zero single route with
   `Engine::install_shard_route_catalog`;
3. create and durably save a `ShardSplit` state;
4. call `ScopedShardEngine::create_split_target`;
5. run range-copy/catch-up pages, record the source high-water and target
   applied frontier, then call `prepare_split_verification`;
6. drain and fence source writes with `WriterFence`;
7. atomically call `install_shard_split_route` and reopen the target through
   `open_shard_participant`;
8. require `validate_shard_target` and `RouteParticipantRegistry::read_cut`
   to succeed before load starts.

This creates two active SQLite files under one database root and one durable
route generation. Additional lanes can be made by repeating the same split on
an admitted route owner, with unique nonzero shard IDs and split IDs, but the
split operations must be sequential and each generation/cutover must complete
before the next starts. Do not write directly to route metadata or publish an
in-memory route.

## Bounded ready task — two lane load fixture

**Owner and writes.** A test/qualification worker owns a new fixture under
`src/engine/` tests (or a dedicated existing test target) and evidence under
`bench/results/<run>`. It may call the existing crate-private setup helpers;
it must not change public API, route contracts, `AGENTS.md`, or benchmark
workload semantics. The single build owner supplies the release artifact.

**Callable setup.** Build one production `Member` with
`AdmissionLimits` allowing at least two resident databases/lanes and enough
RSS/SQLite connection budget. Acquire the source lease, seed documents whose
`canonical_shard_key(collection, id)` falls on both sides of a selected upper
bound, and execute the `admitted_target` sequence with real copy pages. Keep
the source and target `Arc<Engine>` handles and source admission lease alive
for the entire load. Construct the serving registry from the durable source
with `RouteParticipantRegistry::load`, then obtain one `AdmittedShardReadCut`
per measured window. The load client must route each key through the captured
router; it must not round-robin arbitrary IDs.

**Correctness acceptance.** Before throughput is recorded, assert:

- source and target identities have the same database incarnation and Region,
  distinct shard IDs, and nonzero distinct lane incarnations;
- the durable route has exactly two contiguous ranges and one new generation;
- every seeded key maps to exactly one route and lands in the corresponding
  participant;
- a write to each range succeeds only through that participant's shard
  admission token;
- `read_cut` captures both participants at one generation and rejects a
  generation mismatch, missing target, route gap, paused/revoked source, or
  duplicate participant;
- after a target write, the source stale copy is excluded and the routed read
  returns the target value exactly once;
- dropping or exhausting the Member resident budget refuses target creation
  before a SQLite file is created (covered by
  `member::physical_shard_open_reserves_member_budget_once_and_releases_with_cache`);
- restart/reload through `RouteParticipantRegistry::load` validates the same
  target identity and route before serving.

Use focused existing selectors `shard_serving_correction_tests`,
`member::physical_shard_open_reserves_member_budget_once_and_releases_with_cache`,
and `member::shard_registry` tests as the safety gate. A direct performance
number is valid only after these pass and the source/executable/schema
manifest is captured.

## Qualification blocker and decision

There is no public server route that provisions a split: `MemberSplitRuntime`
exists in `src/member/split_runtime.rs`, but no production server caller was
found. Its `start` requires an injected authenticated
`SplitAdmissionAuthority`, existing durable route, and orchestrator. Therefore
the current HTTP benchmark cannot create multiple lanes by API. Root/Astra
must choose one of two bounded paths:

1. run a crate-owned qualification harness that provisions lanes before
   starting the existing HTTP load, retaining the resulting route/identity
   evidence; or
2. wire a real authenticated split control-plane caller and contract, which is
   a larger product scope and must not be invented in a performance task.

The first path is the minimal route for measuring engine/serving throughput.
It still must use the durable sequence above and keep provisioning time out of
the measured window. A single local member remains single process/resource
envelope evidence; it must not be labeled RF2 or cross-region.

## Failure cases and return

Record explicit setup failure for target budget refusal, malformed identity,
generation mismatch, route gap, incomplete copy frontier, stale split state,
or failed verification. Never fall back to source-only service or mark an
unprovisioned target as a second lane. Return the fixture entrypoint, exact
split IDs/shard IDs, route generation, identity fingerprints, Member limits,
focused test commands/results, and evidence paths to the build/benchmark owner.
