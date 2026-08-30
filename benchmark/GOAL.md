# TerraBaseV2 production goal

Status: pre-production plan. Last documentation audit: 2026-08-29, using the
`develop` audit baseline at `4075908`. Later branch tips require a new audit.

This file is the repository execution plan. The workspace-level TerraScale
charter remains the product and architecture authority; repository ADRs record
decisions that need durable local context. No statement in this file is a
production qualification, benchmark result, or release authorization.

## Goal and scope

Deliver a release candidate of the native TerraBaseV2 engine that can be
qualified for the TerraScale production gateway, with reproducible artifacts,
typed failure behavior, durable recovery, secure protocol boundaries, and
evidence for every supported deployment shape.

In scope:

- the root `terrabase-v2` engine library and its eight Cargo workspace crates;
- durable storage, WAL/LSM, indexes, queries, families, sessions, streaming,
  replication, sharding, backup/restore, deletion, usage, and authorization;
- private TBEP and its generated bindings, transport, vectors, and hostile
  decode behavior;
- repository-local host, CLI, and Studio migration/conformance lanes; and
- the receipts and release documentation needed to hand work to the owning
  gateway, control-plane, SDK, benchmark, and operations repositories.

Out of scope for this repository's implementation:

- customer authentication or billing authority;
- OCI provider credentials, provider-specific placement configuration, or
  public API ownership;
- claiming that a local deterministic lane is a WAN, hardware, timing, or
  production result; and
- treating the repository-local trusted-header host or direct TBEP mode as a
  public customer surface.

## Evidence-backed current state

| Area | Current fact | Evidence and limit |
| --- | --- | --- |
| Engine boundary | `src/lib.rs` exports the native engine modules; `Engine::open` with a durable location is the documented production path. | `src/lib.rs`, `src/engine/mod.rs`; implementation status is still active development. |
| Local durability | `LocalDurability` distinguishes `Buffered`, `Written`, and `Synced`. | `src/types.rs`, engine tests; these are local storage points, not replication or backup guarantees. |
| Storage/recovery | WAL/LSM, compaction, snapshots, backup/restore, recovery, tiers, and deletion-domain code have focused tests and receipts. | `docs/evidence/recovery/`, `docs/evidence/p7-*`, `docs/evidence/tier-combination-matrix.md`, `docs/evidence/deletion-workflow-lane.md`; execution wiring and production infrastructure remain open where receipts say so. |
| Replication/cluster | Native replication, member transport, cluster durability, membership, and bounded model-checking lanes exist. | `src/replication/`, `crates/terrabase-cluster/`, `docs/evidence/replication/`, `docs/evidence/model-checking-lane.md`; lanes are in-process or loopback and do not qualify WAN or OCI behavior. |
| Regions | The engine has strict `RegionCode` parsing/catalog behavior for `GRU`, `SFO`, and `FRA`, plus a 20-Region ceiling. | `src/regions.rs`, `docs/evidence/region-catalog-receipt.md`; external admission, placement, routing, and capacity qualification remain open. |
| Protocol | TBEP registry, server/client paths, vectors, negotiation, transport, and hostile-input tests exist; the private schema work is present at the audit baseline. | `crates/terrabase-tbep/`, ADR 0025, qualification rows 26–28; generated-code drift, full cross-language vectors, and all required hostile classes still need release evidence. |
| Product host | `host/` is documented as a migration-source/conformance lane. It uses a trusted local context gate and truthfully refuses engine-backed operations that are not wired. | `host/README.md`, `docs/design/host-retirement-decision.md`; the independently deployable Database service in `TerraScale.Internals` is the intended product gateway, but its migration is cross-repository work. The standalone `TerraScale.Database` repository is transitional and cannot remain source-authoritative. |
| CLI/Studio | The CLI and Studio have typed contracts, fixture lanes, and tests, but their documented host/direct modes are not a public production boundary. | `cli/README.md`, `studio/README.md`; runtime gateway authority is accepted in ADR 0034, while migration/retirement evidence remains open. |
| Qualification | At the 2026-08-29 audit baseline, the 40-row matrix records 2 lane-covered, 26 partial, 8 missing, and 4 blocked-external rows. | `docs/evidence/qualification-gap-sweep.md`; counts are baseline lane status, not GA status. |

The current state is therefore **not production-ready**. The largest gaps are
cross-repository gateway completion, production identity and authorization,
protocol generation/conformance, architecture and WAN qualification, and
durable operation wiring. Existing focused green tests are preserved as
evidence with their stated limits; they must not be promoted to broader claims.

## Target and non-goals

The target follows ADR 0033: a General Availability release qualified on
64-bit Linux x86_64 and ARM64, the admitted three-Region shape (`GRU`, `SFO`,
`FRA`), the declared durability levels, the public Database API, production
security controls, backup/restore, operations, and a continuous thirty-day
soak. ADR 0025 governs the private FlatBuffers/public MessagePack split and
ADR 0031 governs N/N-1 rolling upgrades and activation boundaries.

ADR 0035 selects the fail-closed one-Region contract: `GlobalDurable` must be
refused before acknowledgement when fewer than two active Regions are
selected. The current cluster implementation's degenerate one-Region receipt
is an implementation gap, not a target product behavior.

The target does not include a public beta as a substitute for qualification,
an unbounded compatibility reader, public FlatBuffers, direct Member access,
silent weakening of durability, invented topology facts, or unmeasured
performance claims. Any future reduction of scope requires an accepted ADR and
an explicit service/product decision.

## Prioritized gap register

The IDs below are repository planning IDs. The qualification row references
are the source of truth for detailed scenario status.

| ID | Priority | Gap and required outcome | Depends on |
| --- | --- | --- | --- |
| TBV2-P0-01 | P0 | Product gateway cutover: move supported typed operations, authorization, channel behavior, and conformance from the migration host into the independently deployable `TerraScale.Internals`-owned Database service; keep trusted headers and direct TBEP out of production. Rows 26, 30–32. | Accepted ADR 0034 boundary; protocol contract; gateway/control-plane owners. |
| TBV2-P0-02 | P0 | Protocol release: finish pinned schema generation, Rust/C# and public-client binding drift checks, complete valid/error vectors, and hostile decode gates before field access. Rows 27–28. | ADR 0025 implementation and generator ownership. |
| TBV2-P0-03 | P0 | Identity and durable operations: land ADR 0027 identities where required, wire ADR 0028 operation resources, enforce ADR 0035's one-Region durability refusal, and connect backup, restore, deletion, migration, tier, movement, and repair work to resumable durable state. Rows 8, 14, 28, 38, 40. | Storage/operation owners; protocol and gateway contracts. |
| TBV2-P0-04 | P0 | Production authorization and encryption: integrate signed grants, policy generations, regional key lifecycle, and fail-closed behavior at the product boundary; prove no secrets or private topology escape. Rows 19, 23–25, 30, 33. | Gateway, Dashboard, Orchestrator, and key-management owners. |
| TBV2-P0-05 | P0 | Deployment qualification: native x86_64/ARM64 artifacts, mixed-architecture interchange, three admitted Regions, Tier-2-only baseline, capacity, fault-domain placement, and production SLO/soak evidence. Rows 1–3, 6–7, 29, 35. | OCI/benchmark/operations infrastructure and signed placement policy. |
| TBV2-P1-01 | P1 | Distributed correctness completion: finish coordinator/member/Region loss at production seams, live NATS/control-plane outage, retained-floor resume, authorization reconnect, slow-consumer terminal closure, and cross-language restart/loss matrices. Rows 9–17, 19–21, 26, 39. | P0 protocol and gateway paths; live test infrastructure. |
| TBV2-P1-02 | P1 | Data lifecycle completion: wire deletion copy execution and authorization holds, Merkle/cluster restore verification, optional-tier production behavior, and sharding hot-range split/movement. Rows 4–5, 22, 37–40. | P0 durable operations and storage/cluster owners. |
| TBV2-P1-03 | P1 | Evidence governance: every release receipt records source commit, command, environment, exact result, and limits; refresh the matrix after each lane and reject stale or deleted receipt references. | All workstreams; release owner. |

No gap is closed by a plan or an accepted ADR alone. A gap closes only when
the owning implementation and its acceptance evidence exist and the matrix is
updated with the honest scope.

## Parallel workstreams and dependencies

The following work can start in parallel. A workstream may publish a receipt
without waiting for unrelated work, but downstream release gates consume only
the exact evidence named by that receipt.

| Workstream | Role | Parallel deliverable | Dependency / hand-off |
| --- | --- | --- | --- |
| A — protocol | TerraBaseV2 | Schema generator, binding drift gate, vector corpus, hostile decode, negotiation, and cross-language conformance. | Start from ADR 0025; hand protocol version and limits to B and D. |
| B — engine | TerraBaseV2 | `TbepHost` operation coverage, ULID identity adoption, durable operation journal, deletion execution, restore verification, and tier/sharding integration. | Can start with current native APIs; consumes A's stable protocol and hands receipts to C/D. |
| C — distributed engine | TerraBaseV2 + Orchestrator | Production-shaped membership, replication, loss/recovery, NATS/control-plane seams, authorization reconnect, and retained-history lanes. | Start with existing in-process lanes; consumes A/B contracts before public-surface tests. |
| D — gateway and clients | TerraScale.Internals Database service + SDK owners | TerraScale.Database gateway migration into the independently deployable `TerraScale.Internals` service, grant/auth integration, SDK/Studio contract alignment, and host retirement preconditions. | Consumes A's contract and B/C operation/failure evidence; the accepted runtime boundary is ADR 0034. |
| E — deployment and operations | Orchestrator + TerraBase.Benchmark | OCI placement/admission, native architecture matrix, measured SLO receipts, backup/restore drills, incident runbooks, key lifecycle, and thirty-day soak. | Requires stable B/C/D artifacts and external infrastructure; no local substitute qualifies it. |
| F — release evidence | Workspace owner | Matrix refresh, receipt index, artifact manifest, compatibility/rollback record, compliance package, and final gate review. | Runs continuously; final sign-off waits for A–E and workspace-owner acceptance. |

Critical path: A → B/D → C/E → F. Workstreams B and C may develop native
correctness in parallel with A, and E may provision infrastructure in parallel,
but no release claim may skip the dependency hand-off or the established
repository-role evidence.

## Ownership and resolved decisions

The runtime boundary is already authoritative across the workspace: [ADR
0034](docs/adr/0034-sole-production-gateway-and-host-retirement.md) records
the independently deployable Database service owned by `TerraScale.Internals`
as the sole production gateway, with `host/` retained only for migration and
conformance until its deletion preconditions pass. The standalone
`TerraScale.Database` repository is a transitional source and deployment lane.
It may be retired only after provenance, parity, and production cutover gates
are evidenced and accepted.

[ADR 0035](docs/adr/0035-one-region-global-durable-semantics.md) resolves the
one-Region durability meaning: `GlobalDurable` is refused before
acknowledgement when fewer than two active Regions are selected. Implementing
that refusal and updating its protocol/conformance evidence is a tracked gap,
not an unresolved product question.

External evidence follows the established repository boundaries:

- TerraBaseV2 owns native engine, TBEP, and focused correctness evidence and
  keeps this qualification matrix current.
- TerraScale.Internals owns the permanent Database service source. The
  independently deployable service remains the production gateway authority,
  including gateway authentication and authorization integration and
  public-boundary evidence.
- The standalone TerraScale.Database repository is transitional. Retire it only
  after an owner-approved receipt proves source provenance and generated-code
  parity, all supported operations and security behavior have cut over to the
  Internals-owned service, its release artifacts and rollback path are
  qualified, and the repository's CI, clients, and documentation no longer
  depend on it as an authority.
- TerraScale.Dashboard owns identity/Better Auth behavior and its evidence.
- TerraScale.Orchestrator owns OCI placement/admission, NATS/control-plane,
  key-management adapters, and deployment evidence.
- TerraBase.Benchmark owns measured performance and interference receipts.
- The workspace owner accepts the final release and compliance evidence.

Open items below are implementation, integration, or qualification gaps under
those roles; this plan does not invent personal assignees or duplicate their
authority.

## Acceptance and evidence contract

Every implementation slice must have focused tests and a receipt when it
changes a release invariant. Receipts state their source commit, exact
commands, environment, pass/fail result, and honest limits. A receipt that
contains timing, throughput, WAN, hardware, compliance, or production claims
must also identify the qualified topology and measurement method from ADR 0026;
deterministic counters are not performance evidence.

Minimum repository gates:

```text
cargo fmt --all -- --check
cargo check --workspace --locked
cargo test --workspace --locked
cargo clippy --workspace --all-targets --all-features --locked -- -D warnings
cargo doc --workspace --no-deps --locked
python3 scripts/check_license.py
./scripts/check_secrets.sh
```

CI additionally runs `scripts/check.sh`. Recovery, replication, compaction,
backup, protocol, and benchmark work runs its exclusive lane where required by
the relevant ADR. Generated code is regenerated from its schema owner and
must produce no unexplained diff.

Acceptance evidence for the production goal must demonstrate:

- all supported operations through the intended gateway with typed errors,
  authorization, idempotency, cancellation, and tenant isolation;
- native x86_64 and ARM64 artifacts, mixed-architecture interchange, complete
  protocol vectors, hostile input rejection, and N/N-1 rollback boundaries;
- local, regional, global, and backup durability with crash/restart, member and
  Region loss, repair, restore, deletion, and durable progress evidence;
- bounded queues, resource/noisy-tenant isolation, authorization changes, and
  retained-history behavior at the public serving surface;
- encryption/key lifecycle, secret scans, public-ingress denial, audit
  redaction, and incident/restore runbooks; and
- ADR 0033 SLO receipts, capacity/placement evidence, real Region pairs, and
  the uninterrupted thirty-day soak.

## Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Stale docs or receipts overstate current behavior. | Refresh the matrix at every landing; link exact paths and commits; preserve explicit limits. |
| Generated schema/binding drift creates incompatible clients. | One schema owner, pinned generator, regeneration diff gate, shared vectors, and versioned activation. |
| Trusted local headers leak into the product boundary. | Keep host migration-only; integrate product authentication and grants before cutover; enforce boundary tests. |
| Local lanes are mistaken for production qualification. | Label all deterministic/loopback receipts; require external architecture, WAN, SLO, soak, and compliance evidence. |
| A partial durable operation repeats or loses work after process loss. | ADR 0028 operation identity, atomic checkpoints, fencing, replay tests, and explicit terminal failures. |
| Cross-repository sequencing stalls release. | Use the workstream dependency table and assign owners before dispatch; do not duplicate authority in this repo. |

## Exit gates

TerraBaseV2 exits this plan only when all of the following are true:

1. ADR 0034 and ADR 0035 remain accepted, every supported product boundary has
   one named authority, and each role has supplied the evidence assigned in
   this plan.
2. The qualification matrix has no `missing` or `partial` row inside the
   declared GA scope; every blocked-external row has its required external
   receipt, or an accepted product decision explicitly removes that scope.
3. The complete repository and cross-repository gates are green from pinned
   release artifacts, including generated-code drift, security, license,
   protocol, recovery, and rollback checks.
4. Production authentication, encryption, tenant isolation, observability,
   deletion/retention, backup/restore, and durable operation progress are
   exercised at the intended public boundary.
5. ADR 0033's architecture, SLO, capacity, incident, compliance, and
   uninterrupted thirty-day soak evidence is reviewed and accepted.
6. The release manifest names the source commit, binaries, schemas, generated
   bindings, configuration, compatibility window, rollback boundary, and all
   receipt identities; the final verdict is reproducible by another operator.

Until these gates pass, the correct status is pre-production and the repository
must not be described as GA-ready.
