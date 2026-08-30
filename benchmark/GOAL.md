# TerraBase production goal

Status: accepted direction recorded; code completion and qualification are incomplete.

Last audited: 2026-08-30 against `develop` revision `9c3a491` and the current
dirty working candidate. Uncommitted code, running commands, and local receipts
are not release evidence. This file covers TerraBase only.

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

## Accepted direction

These entries come from repository rules, accepted ADRs, current source, and
the workspace owner's 2026-08-30 direction.

| Area | Accepted direction |
| --- | --- |
| Implementation language | The engine, Studio, benchmark driver, and repository-owned runtime services use Rust. The repository-local `terrabase-go` client and its generated Go gRPC bindings are the explicit exception. The production Database gateway is implemented in Go and uses this client. |
| Engine | TerraBase owns storage, WAL, compaction, indexing, query execution, causality, CRDT merge, replication, repair, membership acceptance, backup, restore, deletion, reactive view semantics, and authoritative usage facts. |
| Private protocol | Private TBEP is protobuf/gRPC over HTTP/2. Production TCP requires TLS 1.3 and mutual workload authentication. `terrabase-go` is the only network client. UDS is local-only. ADR 0025 owns the contract. |
| Reactive semantics | TerraBase owns the transport-independent reactive result-view state machine and exposes it only to the Go gateway through private TBEP gRPC. It has no public transport or codec. |
| Durability | `GlobalDurable` fails closed for fewer than two active Regions. No local or single-Region acknowledgement may be presented as Region-loss RPO 0. |
| Performance evidence | The Rust `bench/` driver and `docs/evidence/benchmarks/benchmark-history.md` are the sole authority in this repository. Raw artifacts remain attached to their exact runs. Performance evidence never authorizes deployment and cannot turn an invalid, failed, safe, or synthetic run into a performance claim. |
| Operations Studio | The private Studio is a Rust/Axum application under `crates/terrabase-studio/` using Datastar, Basecoat, and Monaco for JSON. It binds to loopback, has no authentication, opens one designated engine location in process, and is never a customer boundary. |
| Observability | Prometheus exposition is private. Metrics use stable bounded labels and never expose tenant, document, query, path, credential, or other unbounded values. The repository owns the Grafana dashboard and alert definitions. |
| Encryption | Production uses OCI Vault and ADR 0030. Local development uses generated local keys with an explicit non-production provider identity. Local keys cannot satisfy production evidence. |
| License | TerraBase and every repository-owned Rust crate use the MIT License. |
| Readiness target | This repository delivers a code-complete candidate that the platform can accept users on. `ts-orchestrator` owns the external OCI, architecture, topology, soak, and promotion runs against the exact candidate. |

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

Pre-production. The refreshed 40-row qualification sweep records 2
lane-covered, 26 partial, 7 missing, 4 blocked-external, and 1 removed/non-goal
scenario (`docs/evidence/qualification-gap-sweep.md`). Public MessagePack,
WebSocket, FlatBuffers, and retired clients are absent by decision, not missing
features.

The working candidate now has the private protobuf/gRPC contract, generated Go
bindings, a cross-process mTLS harness, typed ULIDs, one-Region
`GlobalDurable` refusal, authoritative usage RPC work, strict release jobs for
the standalone Studio, bounded Prometheus primitives, alert rules, a Grafana
dashboard, and an offline Monaco JSON editor. These are implementation facts
until the final gates pass on one stable revision.

The executable audit also found release-blocking composition gaps:

- `EngineHost` serves durable documents, queries, schemas, tiers, and usage,
  but still refuses family, backbone, and sync-session RPCs.
- `SyncDeliveryHost` serves backbone and sync state in memory and refuses the
  durable engine plane. No production host composes it with the engine journal.
- `ClusterTbepHost` serves clustered document operations and topology while
  refusing most other engine planes. The production mTLS harness currently
  hosts `EngineHost`, not a complete clustered Member.
- ADR 0030 defines encryption, but the engine has no at-rest encryption or key
  provider implementation. Local generated-key persistence and the production
  FIPS cryptographic module are not selected in code.
- TBEP requests now feed the bounded metric registry, but the full ADR 0036
  signal catalog and the private production listener still need completion.
  `ts-orchestrator` owns listener configuration and deployment qualification.
- `terrabase-server` now exists as the daemon composition, but its first draft
  uses `EngineHost`, cannot become ready without cluster health, and does not
  enforce its configured gateway-principal allow-list, message bound,
  in-flight bound, request timeout, or TLS-handshake timeout. TLS 1.3 and the
  authenticated monitoring boundary also need explicit runtime tests.
- The checkout is changing under several concurrent implementation and test
  processes. No clean full-gate result is recorded for the resulting candidate.

## Gap register

| ID | Priority | Gap | Completion evidence |
| --- | --- | --- | --- |
| PRG-P0-01 | P0 | Remove the retired protocol, gateway, Studio, benchmark, and license directions from current code and documentation. | ADRs, code, tests, CI, and qualification rows name only the accepted 2026-08-30 direction; historical receipts are retired as whole evidence records where necessary. |
| PRG-P0-02 | P0 | Ship one complete production Member host. | One Rust composition serves clustered documents, families, queries, schema, sync/backbone, replication, tiers, usage, maintenance, health, and shutdown against the same durable engine and journal. No required RPC falls through to a default refusal. |
| PRG-P0-03 | P0 | Finish the private protobuf/gRPC TBEP contract. | One pinned schema, Rust serving and generated Go bindings, schema digest, valid/error/hostile vectors, live mTLS and UDS interchange, and rollback evidence. |
| PRG-P0-04 | P0 | Implement ADR 0030 encryption and key lifecycle. | Encrypted WAL, segments, manifests, snapshots, backups, and checkpoints; local generated-key provider that production rejects; OCI Vault provider seam; zeroization, nonce, tamper, wrong-key, rotation, recovery, and FIPS-mode evidence. |
| PRG-P0-07 | P0 | Complete TerraBase durable operations and release invariants. | ULID operation identities, resumable journals, storage/recovery, deletion, backup/restore, membership, replication, repair, and model-check receipts. |
| PRG-P0-08 | P0 | Make every repository-owned deliverable part of the strict release gate. | Clean formatting, warning-free Clippy, tests and docs for the root workspace, Studio, benchmark driver, and `terrabase-go`, plus schema/binding drift and dependency checks. |
| PRG-P0-09 | P0 | Implement production observability. | Prometheus exposition contract, stable bounded labels, redaction tests, health/readiness separation, Grafana dashboard and alerts, failure tests, and an operator runbook. |
| PRG-P1-03 | P1 | Qualify the GRU, SFO, and FRA engine topology. | RF=2 TerraBase placement, independent failure domains, Member and Region loss, and recovery receipts. |
| PRG-P1-05 | P1 | Establish decision-grade performance and isolation evidence. | Approved benchmark policy and baseline, noisy-tenant budgets, workload-family coverage, architecture results, capacity, cost, and regression reports. |

## Owner decisions still required

These choices change implementation or GA scope and cannot be inferred from a
local test:

1. Does one production Member process own the clustered engine, sync delivery,
   family plane, usage, and metrics, or may these be separate Rust processes?
   One process is the current recommendation because one durable journal must
   order writes and reactive delivery.
2. Which in-process FIPS cryptographic module is approved for AES-256-GCM and
   HKDF on both x86_64 and ARM64: AWS-LC FIPS, OpenSSL 3 FIPS, or another named
   provider? OCI Vault protects wrappers but does not encrypt every local WAL
   record for TerraBase.
3. Is a production Member a shared multi-tenant engine location, one location
   per database, or one process with several isolated locations? This fixes
   key-cache, noisy-tenant, backup, and failure boundaries.
4. Must GA rolling upgrades support only the immediately previous released
   format and protocol minor, or a longer window? Greenfield removes legacy
   compatibility, but a live N/N-1 rollout still needs a precise rule.
5. Should reactive checkpoints derive only from the durable engine publication
   journal? The current separate in-memory `SyncDeliveryHost` cannot recover
   sessions or prove that delivered state follows committed storage.
6. Are every currently modeled Data Family and the advanced vector/spatial
   query modes in first-GA scope? Several types reserve later implementations.
   If not, the GA feature manifest must reject them as unsupported instead of
   presenting them as partially available.
7. Should local generated keys persist in an owner-only file beside each engine
   location, or come from a developer keyring? Regenerating them on every start
   would make local durable data unrecoverable.
8. Does Studio require typed confirmation for delete, restore, topology, tier,
   and compaction actions even though it is loopback-only and owner-only?
9. Will the private metrics listener use the same workload CA as TBEP mTLS, a
   separate monitoring CA, or OCI workload identity at a sidecar boundary?
10. Does "ready to accept users" mean a code-complete release candidate handed
    to `ts-orchestrator`, or GA after the mandatory thirty-day soak, SOC 2 Type
    I report, incident drills, capacity reservations, and both native
    architectures pass? ADR 0033 currently requires the latter for GA.

## Parallel workstreams

Streams may start in parallel when their input contract is available. No
stream edits another stream's receipts or changes a dependency to make its own
status appear green.

| Stream | Work that can proceed in parallel | Required handoff |
| --- | --- | --- |
| A. Contracts and generation | Selected private TBEP schema, generation, errors, limits, compatibility, and drift tooling | Versioned schemas, digests, vectors, and activation rules |
| B. Engine correctness | Storage, durable operations, replication, membership, repair, deletion, backup/restore, sharding, and model checks | Typed operation and failure receipts to consuming services |

## Dependency waves

### Wave 0: contract baseline

1. Accept one current ADR index and this `GOAL.md`.
2. Remove the superseded protocol and Studio directions from current authority.
3. Freeze the protobuf/gRPC TBEP, error, limit, and compatibility manifests.
4. Keep all unconfigured production dependencies fail-closed.

### Wave 1: independent P0 implementation

Run protocol generation, engine correctness, and durable-operation work in
parallel against the Wave 0 contract revision, publishing focused evidence
without claiming cross-system qualification.

### Wave 2: integration

1. Run the selected generated private TBEP contract over its real authenticated
   production transport at the TerraBase boundary.
2. Run the reactive result-view reference semantics and corpus through the
   Rust reference implementation and private gRPC host.

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
