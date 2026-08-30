# Decision record: retiring the repository-local C# host lane

Status: analysis record, 2026-08-29. This document gates a later `host/`
deletion packet; it changes no code. Parents: workspace `GOAL.md`
("System ownership" — the `TerraScale.Internals`-owned
`TerraScale.Database` API is the sole TBEP gateway; P6
"move the public product boundary"), `host/README.md` (marked
migration-source at 661ce9a), and the native-codec test alignment at
cddadfd.

Everything below was verified against the working tree on 2026-08-29
(engine repo HEAD fb7f598 — which includes the SE-11 tracer retirement
at bafabd2 and its note at fb7f598 — and `TerraScale.Database` at
1c17026 plus, at writing time, one in-flight uncommitted AuthStorage
lane; the wave-3 document-write vertical (6775ef5) and the
`tests/TerraScale.Database.Conformance` suite (12a617f..038e1dd) landed
after this inventory was taken — see "Honest state" and re-verify §2
before quoting counts). Counts in this record were corrected on
2026-08-29 after independent review (31 routes; 6 `sync.*` and 7
`tier.*` registry operations; client coverage 31/41).

The current ownership decision is separate from this historical inventory.
`TerraScale.Internals` permanently owns the independently deployable Database
service source and runtime authority. The standalone `TerraScale.Database`
repository is transitional and may be retired only after provenance, generated
binding parity, supported-operation cutover, rollback, and CI, client, and
documentation gates are evidenced and accepted. References below to that
repository describe the transitional lane at the recorded snapshot, not a
competing source owner.

## 1. What `host/` is today

Four projects under `host/` (none in the Rust workspace):

| Path | Contents |
| --- | --- |
| `host/TerraBase.Protocol/` | SE-14 TBEP client library: `TbepClient.cs` (typed methods, split with `TbepChannel.cs`, covering 31 of the 41 registry operations — everything except `tier.explain`, `tier.run`, `tier.pause`, `tier.resume`, and the six `replication.*` operations — plus `channel.negotiate` as a channel frame), `TbepChannel.cs` (one engine connection multiplexed between concurrent typed calls and unsolicited backbone notifications, with the error-routing contract), `TbepQuic.cs` (QUIC+TLS binding, ALPN `terrabase.engine.protocol/1`, test-only trust-all), `TbepRegistry.cs`/`TbepValue.cs`/`TbepWire.cs`, `Generated/TbepRegistry.g.cs`. UDS and QUIC bindings. |
| `host/TerraBase.Api/` | ASP.NET Core loopback host (default `127.0.0.1:8420`): `TypedApi.cs` (31 routes: 12 GET, 15 POST, 2 PUT, 2 DELETE), `EngineSupervisor.cs` (spawns/watches/restarts the engine process or attaches a socket), `BackboneHub.cs` (SSE/WebSocket projections, bounded queue depth 64 with counted surfaced drops), `AuditLog.cs` (bounded 1024-record ring), `Dto.cs`, `TerraBaseHost.cs`, `Program.cs`. |
| `host/TerraBase.Api.Tests/` | 67 `[Test]` rows across 12 files, including real-engine rows: `RealEngineTests.cs` (8), `EngineSpineTests.cs` (6 — restart survival, version/capability negotiation, tier administration, QUIC+TLS drive, atomic batches, hard-kill resume), `SyncGoldenVectorTests.cs` (9 — SE-04 corpus), `SupervisionTests.cs` (4), plus `GateTests.cs` (10), `StreamsTests.cs` (13), `SyncSessionTests.cs` (8), `SchemaSurfaceTests.cs` (5), `EngineProbesTests.cs` (2), `ChannelBindingTests.cs` (1), `EntryPointTests.cs` (1). Harness: `ScriptedTbepEngine.cs` (981-line scripted engine), `EngineHarness.cs` (builds the real engine binary from `crates/terrabase-tbep` via a throwaway cargo project), `HostHarness.cs`, `Streams.cs`. |
| `host/TerraBase.Protocol.Tests/` | 7 `[Test]` rows: `TbepGoldenVectorsTests.cs` (4), `TbepChannelRoutingTests.cs` (2), `TbepClientInterleavingTests.cs` (1). |

HTTP surface of `host/TerraBase.Api` (`TypedApi.cs` lines 46–81):

- Documents/queries/families: `GET|PUT|DELETE /v1/documents/{collection}/{document}`, `POST /v1/query`, `POST /v1/query/cancel`, `POST /v1/families/{family}/{name}/read|write`.
- Schema/migrations (SM-04): `GET|PUT /v1/schemas/{collection}`, `POST /v1/schemas/{collection}/validate`, `GET /v1/migrations/{collection}`, `POST /v1/migrations/{collection}/dry-run|apply`.
- Streams: `GET /v1/streams` (SSE), `GET /v1/streams/ws` (WebSocket), `POST /v1/streams/{id}/ack`, `DELETE /v1/streams/{id}`.
- Sync sessions: `POST|GET /v1/sync/sessions`, `POST /v1/sync/sessions/{id}/delta|snapshot|ack|mutations|close`.
- Engine/supervision: `GET /v1/engine/health|status|topology`, `POST /v1/engine/shutdown`, `GET /health`, `GET /v1/metrics`, `GET /v1/audit`.

The engine registry (`crates/terrabase-tbep/schema/tbep-registry.json`,
identity `terrabase.engine.protocol`, registry 1.3, wire 1) defines 41
operations and 3 notifications (`subscription_batch`,
`trigger_notification`, `subscription_closed`). The host client
implements 31 of the 41 operations: documents, query, families,
backbone, all six `sync.*` operations, topology/admin, supervision,
schema/migration, and three of the seven `tier.*` operations
(`configure`, `inspect`, `dry_run`); `tier.explain|run|pause|resume` and
the six `replication.*` operations have no host client code. Channel
negotiation is a channel frame, not an operation.

Trust model (by design, and explicitly slated for removal): there is no
authentication; callers present `X-TB-Principal`/`X-TB-Tenant` headers
injected by a trusted local backend. `host/README.md` states this must be
removed after contract migration and must never become public.

## 2. What the standalone `TerraScale.Database` snapshot covered

`/home/mariogk/Projects/TerraScale/TerraScale.Database/src/**` — six
committed slices (a74d6bc bootstrap, b7e666b `document.read` vertical
slice, 5044229 grant verification, 6f4ca2a QUIC mTLS replacing local
sockets, e845842 usage export domain, 1c17026 registry re-vendor):

- Endpoints wired in `Composition/DatabaseApiComposition.cs` (lines
  51–53): `GET /health/live`, `GET /health/ready` (fail-closed),
  `GET /api/v1/databases/{database}/collections/{collection}/documents/{documentId}`,
  and `POST /api/v1/auth/storage/insert-unique|compare-and-swap|consume-one`
  (this last group is the uncommitted in-flight lane — see below).
- Endpoint code that exists but is not wired:
  `GET /api/v1/tenants/{tenantId}/projects/{projectId}/databases/{databaseId}/regions/{regionId}/partitions/{partitionId}/usage/segments`
  (`Endpoints/Usage/UsageEndpoints.cs` — `UsageEndpoints.Map` is called
  nowhere; the file's own comment says composition must register the
  trusted engine source and authorizer first).
- TBEP adapter (`Adapters/Engine/TbepEngineChannel.cs`): authenticated
  QUIC with TLS 1.3 mutual authentication, custom trust roots, client
  certificate, ALPN `terrabase.engine.protocol/1`, no trust-all mode.
  Supports exactly two operations: `channel.bind` (per-database tenant
  binding) and `document.read`. The channel is strictly serial (one
  semaphore, one in-flight call, `MaxInboundBidirectionalStreams = 1`);
  it has no notification path — a frame whose causation does not match
  the pending call is an error.
- Contracts (`Adapters/Contracts/`): `Generated/TbepRegistry.g.cs`
  byte-identical to engine generation (regenerated at 1c17026), plus
  `TbepRegistry.cs`, `TbepWire.cs`, `TbepValue.cs` ported verbatim from
  the host protocol library (same `TerraBase.Protocol` namespace, per
  its README).
- Security: `DatabaseAccessGrantVerifier` (signature evidence port,
  replay port, policy bounds) with tests — a component only; it is not
  composed into any request path. No OIDC/JWKS/introspection wiring yet.
- Tests (`tests/TerraScale.Database.Tests/`): `DocumentReadTests`
  (scripted-peer e2e, tenant binding, typed refusals, fail-closed
  unreachable engine), `HealthEndpointTests`, `ConfigurationTests`,
  `ContractBoundaryTests` (descriptor metadata only),
  `DatabaseAccessGrantVerifierTests`, `UsageContractTests`,
  `AuthStorageTests` (uncommitted). `ScriptedTbepPeer` is an ephemeral
  mTLS QUIC peer with a generated test PKI. There are no real-engine
  rows.

Honest state: `TerraScale.Database`'s working tree carries an uncommitted
AuthStorage lane (`src/TerraScale.Database.Application/AuthStorage/`,
`src/TerraScale.Database.Domain/AuthStorage/`,
`src/TerraScale.Database/AuthStorage/`, composition edits, and
`tests/.../AuthStorageTests.cs`). The three auth-storage routes and their
default `UnavailableAuthStorageStore` (503) are therefore in-flight, not
landed; do not cite them as committed capability.

## 3. Per-capability table

"Host" = `host/` today; "Database" = `TerraScale.Database` at 1c17026
(+ noted in-flight lane).

| Capability | host/ has | TerraScale.Database has | Gap |
| --- | --- | --- | --- |
| TBEP transport | UDS + QUIC+TLS; QUIC binding permits a test-only trust-all (`TbepQuic.cs`) | QUIC mTLS only, mandatory verification, no trust-all | None for product (mTLS is the target); UDS dropped deliberately |
| Registry bindings | `Generated/TbepRegistry.g.cs` machine-checked by engine drift test | Same file vendored byte-identical; **no automated drift gate** (manual re-vendor procedure only) | Database-side drift gate missing |
| Channel binding / tenant isolation | `channel.bind`, first-bind discipline, engine-enforced cross-tenant refusals (tested) | `channel.bind` per database segment (tested against scripted peer) | Real-engine isolation rows not re-homed |
| Concurrent calls + notifications | `TbepChannel` multiplexes typed calls and unsolicited backbone frames; error-routing contract tested | Serial one-in-flight channel; unmatched frames are errors | Full gap |
| Version/capability negotiation | `channel.negotiate` (negotiation channel frames), version-skew behavior tested against the real engine | None (wire 1 assumed) | Full gap |
| `document.read` | Route + client op | Public `/api/v1` route end to end over QUIC mTLS | Covered |
| `document.write`/`delete`/`batch` | Routes + client ops; atomic batches proven against the real engine | None | Full gap |
| `query.execute`/`query.cancel` | Routes + client ops | None | Full gap |
| `family.read`/`family.write` | Routes + client ops | None | Full gap |
| Backbone subscriptions + SSE/WS streams | `backbone.subscribe|acknowledge|unsubscribe`, two stream projections, bounded queue with counted drops, checkpoint resume | None | Full gap |
| Sync sessions (`sync.*`, 6 ops) | Full session lifecycle routes (7 HTTP routes); checkpoint refusal/resync semantics; 9 SE-04 golden vectors replayed end to end against the real engine | None | Full gap (largest single gap) |
| Schema/migrations (6 ops) | Routes + client ops (SM-04) | None | Full gap |
| Tier administration (7 registry ops; client implements 3) | Client ops for `configure`/`inspect`/`dry_run` + real-engine tier rows | None | Full gap |
| Topology/admin/supervision | `topology.read`, `admin.status`, `supervision.health|shutdown`, `EngineSupervisor` process lifecycle | None | Deliberate non-goal for the product gateway (see §4) |
| Local gate / principal headers | Trusted `X-TB-Principal`/`X-TB-Tenant` gate (no auth, by design) | None — correct; the gateway instead has grant verification (unwired) and OIDC integration pending | Not a migration item; the header gate must die with `host/` |
| Idempotency-key HTTP discipline | Required on every mutation route | Required on auth-storage mutations only (in-flight) | Extends with each mutation op |
| Engine-process supervision/restart | Spawn/watch/restart, truthful health, generation loss, graceful drain | None | Dev-lab aid; do not migrate as a public surface |
| Usage export | None | Domain + application + route code (unwired) + tests | Database-only direction |
| Better Auth storage ops | None | Port + 3 routes + tests (store defaults unavailable; in-flight lane) | Database-only direction |
| Grant/authorization verification | None (no auth surface) | Verifier + tests, not composed; OIDC/JWKS/introspection absent | Database-only direction |
| Real-engine e2e coverage | 27 rows across RealEngine/EngineSpine/SyncGoldenVector/Supervision | None (scripted peer only) | Full gap |
| Cross-language codec conformance | `TerraBase.Protocol.Tests` golden vectors + channel routing | `ContractBoundaryTests` metadata only | Full gap |

## 4. Classification

- **Migrate** (public-contract value): typed operation coverage
  (documents write/delete/batch, query, families, schema/migrations,
  tier), sync session surface, backbone/streams, channel multiplexing +
  notification handling, negotiation, golden-vector conformance,
  real-engine e2e discipline, idempotency discipline.
- **Replace, not migrate** (plumbing whose ownership moves): the C#
  registry artifact and its drift enforcement; the CI lane that builds
  and tests the C# side; the CLI's transport.
- **Do not migrate** (host-only by design, dies with the lane): the
  trusted-header local gate, the loopback-only binding, UDS binding,
  engine-process spawning/shutdown administration as a public route.
  GOAL.md assigns workload supervision to the Orchestrator, and the
  engine-context trust path replaces the header gate.

## 5. Preconditions for deleting `host/`

The deletion packet may not merge until every item below is true.

1. **Database-side registry drift gate exists.** Today the only
   machine-checked C# registry artifact is the host copy, enforced by
   `crates/terrabase-tbep/tests/registry_drift.rs`
   (`committed_csharp_bindings_equal_regeneration`, lines 48–61).
   Nothing in this recorded snapshot verified the Database API's vendored copy
   against the schema. Add a gate in the `TerraScale.Internals/src/Database`
   CI lane (compare the vendored
   `Generated/TbepRegistry.g.cs` against a pinned copy of
   `crates/terrabase-tbep/schema/tbep-registry.json` output, or an
   equivalent cross-repo pin) before deletion.
2. **Engine codegen re-pointed.** `crates/terrabase-tbep/src/codegen.rs`
   line 29 (`CSHARP_OUTPUT = "host/TerraBase.Protocol/Generated/TbepRegistry.g.cs"`),
   `crates/terrabase-tbep/tools/tbep-gen/src/main.rs` (writes that path),
   and the drift test above must stop referencing `host/`. Cross-repo
   reality: the engine CI checkout does not contain
   `TerraScale.Internals`, so the C# drift assertion must move to the
   Internals-owned Database side (item 1) or the generator drops its C# output
   target.
   This needs an explicit decision in the deletion packet.
3. **CLI re-pointed.** `cli/TerraBase.Cli/TerraBase.Cli.csproj` line 32
   references `host/TerraBase.Protocol`, and the CLI's default `--mode
   host` drives the host HTTP surface at `127.0.0.1:8420`
   (`cli/TerraBase.Cli/Cli.cs`). The CLI must vendor or otherwise obtain
   its protocol client and target a supported surface (or the CLI
   surface is itself retired by owner decision).
4. **CI rewired.** `.forgejo/workflows/ci.yml` `dotnet` job (lines 59–66)
   builds and tests `host/TerraBase.Protocol[.Tests]` and
   `host/TerraBase.Api[.Tests]`. Those steps must be removed in the same
   change as the deletion; the CLI steps stay only if item 3 resolves.
5. **Conformance coverage re-homed, not dropped.** All of the following
   must exist in `TerraScale.Internals/src/Database` (or, where noted,
   engine-side)
   before deletion:
   - SE-04 sync golden vectors (the 9 JSON files under
     `crates/terrabase-sync-core/vectors/terrascale.sync.v2/`, currently
     copied into the host test project via its `.csproj` lines 38–40)
     replayed through Database-owned code against the real engine. This
     presupposes the sync/reactive public surface exists there.
   - SE-14 protocol conformance: golden vectors, channel error routing
     (an error for an abandoned call completes nothing; a causation-less
     decode refusal fails the oldest pending call), concurrent-call
     interleaving — which requires the Database channel to support
     multiplexing first.
   - Real-engine e2e rows currently unique to the host:
     binding/isolation, typed operation refusals, native delivery,
     retained-floor resync, backbone fan-out, graceful shutdown, the
     decode-refusal regression, process-loss resume, restart survival,
     negotiation, tier administration, atomic batches, QUIC+TLS drive
     (`EngineSpineTests`).
   - Cross-language codec evidence required by ADR 0025/0026 must cite a
     live C# lane; after deletion that lane is the Database one.
6. **Evidence and contract records updated in the same change.** See §7
   for the citation list. Historical receipts that cite `host/` as their
   harness are either marked superseded or deleted per the GOAL.md rule
   that obsolete records are deleted, not maintained.

## 6. Recommended sequencing

- **Phase A — build the receiving surface (no deletion).** In the
  `TerraScale.Internals`-owned Database API under `src/Database`: extend the
  channel to multiplexed calls with a
  notification path and negotiation; land the typed operation families
  behind `/api/v1` with the grant/OIDC admission path; land the sync and
  stream surfaces; add a real-engine test harness (the host's
  `EngineHarness` pattern — build the engine binary from
  `crates/terrabase-tbep` into a throwaway cargo project — ports
  directly); replay the SE-04 corpus there; add the vendored-registry
  drift gate. CI-green each slice.
- **Phase B — move the dependencies.** Re-point the engine codegen
  output and drift test (decision from §5 item 2), re-point or retire
  the CLI, rewire the engine CI `dotnet` job. Engine CI stays green with
  no `host/` reference.
- **Phase C — delete.** Remove `host/` (all four projects plus
  `host/README.md`) and the retired SE-11 remnant that still drives it
  (`scripts/se11/run.py`, whose topology builds and supervises
  `host/TerraBase.Api`; the `tracer/` half of that lane was already
  deleted at bafabd2), and prune the stale citations in §7. One packet,
  referenced to this record.

Deletion is explicitly **not** recommended now: the sync surface, query,
mutations, and the entire real-engine conformance discipline exist only
under `host/`, and the engine's own CI gate depends on a file inside the
lane.

## 7. Risk notes — what cites `host/` today

Machine-checked dependencies (deletion breaks these silently if missed):

- `crates/terrabase-tbep/src/codegen.rs:29` — `CSHARP_OUTPUT` constant.
- `crates/terrabase-tbep/tools/tbep-gen/src/main.rs:11` — generator
  writes the host path.
- `crates/terrabase-tbep/tests/registry_drift.rs:10,49–61` — drift test
  reads the host artifact; part of engine CI.
- `.forgejo/workflows/ci.yml:59–66` — builds/tests both host projects.
- `cli/TerraBase.Cli/TerraBase.Cli.csproj:32` — project reference; the
  CLI does not build without it.
- `cli/TerraBase.Cli.Tests/TerraBase.Cli.Tests.csproj:32,37` —
  ProjectReferences to `host/TerraBase.Api.csproj` and
  `host/TerraBase.Api.Tests.csproj`; the CLI test project (built and run
  by engine CI) cannot compile after deletion.
- `studio/scripts/real-host-probe.mjs` (~157 `buildHost()`, ~358 spawns
  `TerraBase.Api.dll`) — functionally builds and boots the host;
  `studio/scripts/fixture-server.mjs:602` and
  `studio/contract/terrabase-api.contract.json:861` name the host's
  `/v1/streams` surface; `studio/tests/changes-subscription.test.mjs:4,326`
  cites `TypedApi.StreamSseAsync` and `BackboneHub.cs`.

Documentation and evidence citations:

- `docs/design/p0-protocol-compatibility-contract.md:13` — names
  `host/TerraBase.Protocol` as the cross-language conformance substrate.
  Its header already says "UDS and repository-local C# hosts below are
  migration history."
- `docs/evidence/p0-loro-sweep-receipt.md:19–20,73–75,113,131,136` — the
  P0 de-Loro receipt records host test runs and greps as its evidence.
- `scripts/se11/run.py:11,65` (and its topology builder below) — the
  retired SE-11 harness still references `host/TerraBase.Api`; the
  `tracer/` half of that lane was deleted at bafabd2, whose evidence
  note `docs/evidence/tracer-retirement-note.md:36,66–67` itself
  censuses `host/` references. The immutable receipts
  `docs/evidence/sync/se11/s1…s6` remain and cite the host harness;
  they are historical Loro-era records, already non-qualifying.
- `docs/storage/gaps1-storage-compaction.md:61` — prose reference to the
  host owning compaction cadence.
- Engine source comments: `src/engine/ingest.rs:67,76`,
  `src/query/bulk.rs:136`, `src/benchmarks/lane.rs:386` — comment-level
  only.
- Cross-repository: the current
  `TerraScale.DotNet/docs/sync/terrascale-sync-v2-client.md` harness citation
  must name `TerraBase/host/TerraBase.Api` while the migration host remains.

Known inconsistencies the deletion packet must not trust:

- `docs/design/p0-protocol-compatibility-contract.md`'s header says
  current TBEP is `terrabase.engine.protocol/2` and points to
  `docs/sync/tbep.md`; that file does not exist, and the code says wire
  generation 1 everywhere (registry 1.3; ALPN
  `terrabase.engine.protocol/1` at `crates/terrabase-tbep/src/quic.rs:32`).
- GOAL.md P6 names generated FlatBuffers contracts for the gateway;
  both current implementations use the generated MessagePack registry
  whose schema owner is `crates/terrabase-tbep/schema/tbep-registry.json`.
  This record treats that file as the operative schema owner and does
  not condition retirement on a FlatBuffers migration.
- `host/README.md` still describes the pre-spine interim ("document/
  query/family answer `operation_refused`") that
  `EngineSpineTests` (landed later) partially superseded; inventory in
  §1 was taken from code, not the README alone.

## 8. Verification commands used for this record

- After the repository rename, the historical revision check is run as
  `git -C TerraBase show --stat 661ce9a cddadfd` — migration-source and
  codec-alignment commits.
- `grep -n "MapGet\|MapPost\|MapPut\|MapDelete" host/TerraBase.Api/TypedApi.cs`
  — route inventory.
- `grep -rc "\[Test\]" host/**.cs` — test-row counts (67 + 7).
- `diff` of both `Generated/TbepRegistry.g.cs` copies — byte-identical.
- Registry parse of `crates/terrabase-tbep/schema/tbep-registry.json` —
  41 operations, 3 notifications, identity/version.
- `grep -rn "host/" --include=*.md --include=*.yml --include=*.sh --include=*.toml --include=*.rs`
  across the engine repo (minus `target/`, `host/`) — the §7 citation
  list, re-run after the working tree fast-forwarded to fb7f598 (SE-11
  tracer retirement) so the tracer citations reflect the post-deletion
  state. Correction pass (post-review) extended the census with
  `--include=*.csproj --include=*.mjs --include=*.json`, which surfaced
  the CLI test project references and the `studio/` citations now listed
  in §7; the original filter would have missed them.
- `grep -rn "UsageEndpoints.Map\|GrantVerifier"` across
  `TerraScale.Internals/src/Database` — confirms the current Database API's
  usage route and grant-verifier wiring state. The historical result recorded
  above—that both were unwired—remains tied to the standalone repository
  snapshot.

No build or test commands were run; this packet changes documentation
only.
