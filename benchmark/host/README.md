# TerraBaseV2 API host (C#)

The database's external surface, in C# per the workspace stack policy
(charter PD-2026-08-26-01/02; ADR 0005): an ASP.NET Core host that is a
TBEP **client** of the Rust engine process over the required
Unix-domain-socket binding (PD-2026-08-26-11 D1), speaking through the
SE-14 `TerraBase.Protocol` client — never a C ABI, never a raw socket,
never storage access (probe-tested red-then-green in
`TerraBase.Api.Tests/EngineProbesTests.cs`).

## Projects

| Project | Role |
| --- | --- |
| `TerraBase.Protocol` | The SE-14 TBEP client (landed with SE-14), plus this packet's `TbepChannel`: one engine connection multiplexed between concurrent typed calls and the unsolicited backbone notifications. |
| `TerraBase.Api` | The ASP.NET host: typed operations, subscription endpoints, sync-session forwarding, engine supervision, lifecycle audit (`TerraBaseHost.Create` is the assembly point; `Program` is the entry). |
| `TerraBase.Api.Tests` | The SE-08 test family (TUnit): the local gate and tenant isolation, SSE/WebSocket parity and resume, the bounded-queue drops, the sync-session lifecycle, the SE-04 golden vectors end to end, real-engine e2e rows, and supervision against real engine-process loss. |

## The boundary (PD-2026-08-26-11)

The host is strictly local: it binds loopback, the engine binds UDS,
and there is **no authentication or authorization surface here by
design** — customer auth and PB-3042 live exclusively at the TerraScale
Platform (no claim is made in this packet). What the host enforces:

- **The local gate** — a caller must present backend-supplied context
  (`X-TB-Principal`, `X-TB-Tenant` headers, injected by the trusted
  local backend). Without it the request is refused (401) before any
  engine frame is sent, and the refusal is audited.
- **Scope validation** — collections, documents, and family names are
  bounded identifiers (422 before any engine frame).
- **Engine-enforced tenant isolation** — the tenant binds the channel
  once (`channel.bind`); every session and subscription lifecycle frame
  of another tenant is refused by the engine (`bound to another
  channel`), surfaced as 403 `tenant-refused`, audited, and the owner
  keeps full ownership.
- **Mutation identity** — every mutation requires a bounded,
  control-free `Idempotency-Key`; the operation id of a mutation is its
  key (the retry identity), so a retried write names the same journal
  operation.

## The surface

Every operation is a typed TBEP operation; JSON surfaces use
`System.Text.Json` only.

- Documents, queries, families: `/v1/documents/{collection}/{document}`
  (GET/PUT/DELETE), `/v1/query`, `/v1/query/cancel`,
  `/v1/families/{family}/{name}/read|write`.
- Subscriptions: `GET /v1/streams` (SSE) and `GET /v1/streams/ws`
  (WebSocket) — two projections of one TBEP backbone subscription.
  Replay from a checkpoint with `?from=N`; SSE reconnects resume
  strictly after the last seen `Last-Event-ID`. Each stream holds a
  bounded queue (depth 64): a consumer a full depth behind drops oldest,
  and the drop is counted, surfaced in-band (the resume sequence), in
  `/v1/metrics`, and in the audit. `POST /v1/streams/{id}/ack`
  acknowledges through a sequence; `DELETE /v1/streams/{id}` closes.
- terrascale.sync.v2 sessions: `/v1/sync/sessions` (setup with optional
  resume checkpoint, listing), `.../{id}/delta|snapshot|ack|mutations|close`.
  Checkpoints and payloads are opaque codec bytes; refusals behind the
  engine's retained floor answer 409 `checkpoint-refused` with
  `resyncRequired: true` (pull a snapshot to resync). Every plain typed
  refusal (410/409/500/503 that demands no resync) omits the member —
  it is serialized only when demanded, never as a null marker.
- Engine reads and supervision: `/v1/engine/health|status|topology`,
  `POST /v1/engine/shutdown`; `/health` (composite, never optimistic),
  `/v1/metrics`, `/v1/audit`.

The engine-backed document/query/family operations answer the engine's
truthful typed refusal (501 `operation_refused`) until the engine-side
`TbepHost` wiring lands (the named integrator action of the SE-14
record) — the sync-delivery family, backbone streams, topology/admin,
and supervision are served by the real engine today.

## Supervision

`EngineSupervisor` owns the engine endpoint: a managed engine process
it spawns, watches, and restarts within a bounded policy, or an
attached socket it neither spawns nor restarts. Supervision is truthful:
health is the live engine state checked on demand; engine-process loss
surfaces as a typed 503 `engine-unavailable` for every caller, closes
every open stream with a resume-able `engine-generation-lost` frame,
ends (never orphans) every tracked session, and — within the restart
budget, re-checked at recovery time — brings a fresh engine generation
back. A graceful shutdown drains, closes every subscription with
`shutting_down`, and never restarts.

The audit log is a bounded ring (1024 records) of enumerable lifecycle
facts — kind, tenant, principal, operation, outcome, bounded detail —
and never a document body, payload, or credential (tested).

## Running

```
TERRABASE_TBEP_SOCKET=/path/to/engine.sock     # default /run/terrabase/engine.tbep.sock
TERRABASE_TBEP_ENGINE=/path/to/engine-binary   # unset = attach to a managed-elsewhere engine
TERRABASE_HOST_URL=http://127.0.0.1:8420       # the loopback listener
```

```
cd host
dotnet run --project TerraBase.Api              # the host
dotnet run --project TerraBase.Api.Tests        # the SE-08 family (80 rows)
dotnet run --project TerraBase.Protocol.Tests   # the SE-14 conformance suite
```

The real-engine e2e rows build the repository's actual engine server
(`TbepServer` over `SyncDeliveryHost` from `crates/terrabase-tbep`)
into a throwaway cargo project outside the repository — nothing in the
repository's Rust scope is touched — and hand the binary to the
supervisor, which spawns, watches, kills, and restarts it over UDS. The
first run compiles it once per worktree (`~1 minute` cold); set
`TERRABASE_TEST_CARGO_ONLINE=1` to allow a networked fetch retry.

## Tests

- `EngineProbesTests` — engine communication only through the TBEP
  client (red-then-green probes: each banned construct is flagged when
  embedded; the host sources are clean).
- `GateTests` — the local gate, scope validation, idempotency
  validation, cross-tenant lifecycle refusals with ownership retained,
  tenant-scoped session registry, audit discipline (no bodies, no
  credentials).
- `StreamsTests` — SSE/WebSocket parity, checkpoint replay and
  `Last-Event-ID` resume, collection filters, the bounded queue with
  counted surfaced drops (the incoming frame survives a shed; offered
  equals delivered plus dropped, exactly; the drop marker carries the
  dropped region's boundary and a reconnect from it re-delivers the
  missed range), the stream lifecycle.
- `ChannelBindingTests` — the first-bind discipline: concurrent first
  callers of one tenant share one live channel (the late arrival adopts
  the first bind's outcome; one bind, nothing disposed).
- `EntryPointTests` — a failed startup owns everything it started: the
  spawned managed engine process is killed and disposed.
- `SyncSessionTests` — the session lifecycle end to end, retry
  receipts, typed composite rejections, checkpoint refusals with the
  resync marker, fail-closed malformed resumes, ended-session answers
  (including the engine ending a session without a generation loss:
  the host stops tracking it).
- `TerraBase.Protocol.Tests` (the SE-14 suite) — the golden-vector
  conformance rows plus the serving channel's error-routing contract:
  an error for an abandoned call completes nothing; a causation-less
  decode refusal fails the oldest pending call.
- `RealEngineTests` — the real engine over UDS: binding and isolation,
  the typed operation refusals, real Loro delivery, retained-floor
  resync, real backbone streams with content-free triggers, graceful
  shutdown, and the decode-refusal regression (a refused frame surfaces
  its typed error; it never hangs).
- `SupervisionTests` — real process loss and recovery: truthful health,
  typed refusals, resume-able stream closes, no orphaned state, the
  bounded restart policy, permanent truth for attached engines.
- `SyncGoldenVectorTests` — the SE-04 golden vectors replayed through
  the host against the real engine (the corpus set is pinned; a new
  vector fails loudly).
