# TerraBase production goal

Status: goal defined; implementation and qualification are incomplete.

Last audited: 2026-08-29. Repository deletions recorded 2026-08-30. This file
was consolidated from the workspace-level TerraScale program goal on 2026-08-30
and covers TerraBase only; the replaced repository `GOAL.md` remains in git
history.

This file does not claim that a Region, artifact, benchmark, security control,
or recovery procedure is production-ready. Repository `AGENTS.md` remains a
hard rule, and repository ADRs record decisions that need durable local
context.

## Goal

TerraBase is the private native multi-Region database engine of the TerraScale
product. It owns storage, WAL, compaction, indexing, query execution,
causality, CRDT merge, replication, repair, membership acceptance, backup,
restore, deletion, reactive view semantics, and authoritative usage facts.
Promote only immutable artifacts whose contract, security, durability,
recovery, performance, and rollback evidence belongs to the exact candidate.

Production begins with GRU, SFO, and FRA. TerraBase uses two active Members
per selected Region and Partition. A database may select one through twenty
admitted Regions. A `GlobalDurable` write is refused when fewer than two
active Regions are selected.

## Accepted decisions

These decisions come from repository rules, accepted ADRs, current source, or
the workspace owner's 2026-08-29 direction. They are not open design questions.

| Area | Accepted direction |
| --- | --- |
| Implementation language | This repository uses Rust only. `AGENTS.md` is a hard rule. |
| Engine | TerraBase owns storage, WAL, compaction, indexing, query execution, causality, CRDT merge, replication, repair, membership acceptance, backup, restore, deletion, reactive view semantics, and authoritative usage facts. |
| Private protocol | Private TBEP is the generated protobuf/gRPC contract of ADR 0028 (`crates/terrabase-tbep/proto/terrabase/v1/engine.proto`), served by tonic over TLS/UDS. The retired MessagePack and FlatBuffers framings (and their generators) are migration input and block production until every consumer cutover is proven. |
| Public reactive protocol | TerraBase supplies the reference semantics and corpus for the bounded public reactive result-view contract. |
| Durability | `GlobalDurable` fails closed for fewer than two active Regions. No local or single-Region acknowledgement may be presented as Region-loss RPO 0. |
| Performance evidence | The `TerraBase.Benchmark` repository was deleted 2026-08-30 by owner decision; its full history is archived at `~/Projects/TerraScale-archive/TerraBase.Benchmark-2026-08-30.bundle`. No decision-grade performance baseline exists, and benchmark capability must be re-owned before PRG-P1-05 can close. Performance evidence never authorizes deployment and cannot turn an invalid, failed, safe, or synthetic run into a performance claim. |

## Evidence rules

Use these status words consistently:

- `implemented`: source and focused tests exist;
- `lane-covered`: a named test or receipt passed in its stated environment;
- `qualified`: the exact production candidate passed the required real
  boundary;
- `blocked`: a required contract, implementation, environment, decision, or
  receipt is missing; and
- `production-ready`: every exit gate in this file has current,
  candidate-bound evidence.

An ADR records a decision. It does not prove implementation. A local fixture,
RF=1 lab, loopback protocol test, skipped live test, synthetic receipt, or
configuration declaration cannot satisfy a production gate.

Every retained receipt must name the source revision, artifact and
configuration identity, exact command or action, environment and topology,
result, evidence tier, known limits, and redaction status. Unknown facts stay
unknown. Failed and invalid results remain visible and cannot be reclassified
as passes.

## Current status

Pre-production. The 40-row qualification sweep records 2 lane-covered, 26
partial, 8 missing, and 4 blocked-external scenarios at the 2026-08-29 audit
baseline (`docs/evidence/qualification-gap-sweep.md`). Finish private protocol
generation, durable-operation wiring, distributed failure behavior,
architecture qualification, and production receipts.

## Gap register

| ID | Priority | Gap | Completion evidence |
| --- | --- | --- | --- |
| PRG-P0-03 | P0 | Finish the private TBEP gRPC contract (ADR 0028). | Pinned protobuf schema as the single contract source, Rust serving + Go client bindings, schema digest, valid/error/hostile vectors, live TLS/UDS interchange, and rollback evidence. |
| PRG-P0-07 | P0 | Complete TerraBase durable operations and release invariants. | ULID operation identities, resumable journals, storage/recovery, deletion, backup/restore, membership, replication, repair, and model-check receipts. |
| PRG-P1-03 | P1 | Qualify the GRU, SFO, and FRA engine topology. | RF=2 TerraBase placement, independent failure domains, Member and Region loss, and recovery receipts. |
| PRG-P1-05 | P1 | Establish decision-grade performance and isolation evidence. | Approved benchmark policy and baseline, noisy-tenant budgets, workload-family coverage, architecture results, capacity, cost, and regression reports. |

## Parallel workstreams

Streams may start in parallel when their input contract is available. No
stream edits another stream's receipts or changes a dependency to make its own
status appear green.

| Stream | Work that can proceed in parallel | Required handoff |
| --- | --- | --- |
| A. Contracts and generation | Private TBEP protobuf schemas, generation, errors, limits, compatibility, and drift tooling | Versioned schemas, digests, vectors, and activation rules |
| B. Engine correctness | Storage, durable operations, replication, membership, repair, deletion, backup/restore, sharding, and model checks | Typed operation and failure receipts to consuming services |

## Dependency waves

### Wave 0: contract baseline

1. Accept one current ADR index and this `GOAL.md`.
2. Freeze the private TBEP, error, limit, and compatibility manifests.
3. Keep all unconfigured production dependencies fail-closed.

### Wave 1: independent P0 implementation

Run protocol generation, engine correctness, and durable-operation work in
parallel against the Wave 0 contract revision, publishing focused evidence
without claiming cross-system qualification.

### Wave 2: integration

1. Run generated FlatBuffers over real authenticated QUIC at the TerraBase
   boundary.
2. Run the public reactive reference semantics and corpus through the Rust
   reference implementation.

### Wave 3: production-shaped qualification

Qualify GRU, SFO, and FRA with native ARM64 and x86_64 artifacts, RF=2
TerraBase placement, independent failure domains, Member and Region loss,
backup/restore, noisy-tenant protection, and rollback. Environment-gated skips
remain blockers.

### Wave 4: performance and soak

Re-own or restore the decision-grade benchmark tooling, then run the approved
performance lanes. Complete the uninterrupted thirty-day candidate soak. A
rebuild or configuration change restarts candidate qualification.

### Wave 5: promotion

Promote the exact qualified candidate and observe the accepted release window.
Promotion does not rebuild the candidate.

## Acceptance gates

1. **Ownership.** One current authority owns each engine source, runtime
   contract, artifact, and evidence record.
2. **Contracts.** TBEP, errors, limits, generated manifests, and compatibility
   rules agree and pass drift checks.
3. **Security.** Encryption and key lifecycle, tenant isolation, replay,
   hostile-input rejection, audit redaction, and secret-scan evidence passes
   at live boundaries.
4. **Durability.** Local, regional, global, backup, restore, deletion,
   membership, repair, and recovery semantics pass their failure matrices.
5. **Runtime protection.** Bounded queues, cancellation, slow-client,
   noisy-tenant, and graceful shutdown behavior passes.
6. **Topology.** GRU, SFO, and FRA satisfy RF=2 placement, independent failure
   domains, and Member and Region-loss recovery.
7. **Performance.** Decision-grade workload, latency, throughput, isolation,
   capacity, cost, and regression evidence meets the accepted SLOs.
8. **Soak and release.** The exact GA candidate completes the required
   thirty-day soak and the final owner review.
