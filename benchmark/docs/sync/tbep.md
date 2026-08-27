# The TerraBase Engine Protocol (TBEP) — `terrabase.engine.protocol/1`

Status: implementation record for the SE-14 packet (issue #10),
2026-08-26. Authority: charter PD-2026-08-26-02 (the custom C#↔Rust wire
protocol mandate), PD-2026-08-26-11 D1 (the UDS binding is the required
local transport; the QUIC binding is deferred until a networked-engine
need is recorded), PD-2026-08-26-12 (native clients; TBEP is the engine's
only interop), PD-2026-08-26-13 D1/D3 (bytes-on-wire budgets; trigger
notifications), PD-2026-08-26-14 D1 (the operation registry's
parity-mapping column, the GAP-A1 fold) and D3 (the GAP-Q12 fold: query
cancellation); ADR 0005 D-2/D-3, ADR 0008. Worktree `terrabasev2-se14`,
branch `fleet/se14-2`, based on `develop` @ `ef72eca`.

This document is the protocol spec: the frame taxonomy, the envelope and
codec discipline, versioning and fail-closed rules, the security model,
the supervision contract, the channel-binding and tenant-isolation
model, and the per-frame envelope-overhead budget. The typed
implementation is the `crates/terrabase-tbep` crate (engine side) and the
`host/TerraBase.Protocol` project (C# client); the golden conformance
corpus is `crates/terrabase-tbep/vectors/tbep/` with its Rust runner
(`tests/vectors_runner.rs`) and its C# runner
(`host/TerraBase.Protocol.Tests`).

## Scope and boundaries

- TBEP is the **engine channel**: the only interprocess surface between
  the Rust engine process and C# hosts (PD-2026-08-26-12 D4). The C#
  API host (SE-08) and the CLI's direct mode (SE-13) both consume the
  `TerraBase.Protocol` client; no other interop exists — no C ABI
  service boundary, no HTTP, no in-process embedding across the service
  boundary.
- TBEP is **not** the client Sync Protocol: `terrascale.sync.v2`
  (SE-04) is the client channel. What TBEP reuses from SE-04 is the
  envelope discipline (identity, causation, idempotency, digest —
  `docs/sync/terrascale-sync-v2.md`), re-profiled onto MessagePack
  frames. The sync-delivery family of TBEP operations serves the
  delivery core (`terrabase-sync-core`, the SE-02 GO decision): a C#
  host terminates client sync sessions and translates them onto TBEP
  sync-delivery frames.
- The engine core stays synchronous and zero-dependency; the transport
  (sockets, threads) lives in the `terrabase-tbep` crate only
  (PD-2026-08-26-05 D1). The engine-backed document/query/family host
  wiring is a named integrator action (below); the shipped serving host
  is the real sync-delivery core over `terrabase-sync-core`.

## Transport bindings and the binding decision record

Two bindings were mandated by PD-2026-08-26-02 D2 over one frame layer:

- **UDS (Unix-domain socket), required**: the local binding. The engine
  process binds a filesystem path with mode `0600` (owner-only); the C#
  host connects as the same user. This is the required transport per
  PD-2026-08-26-11 D1 — the engine/host boundary is strictly local, and
  no authentication or authorization surface exists there by design:
  customer authentication and PB-3042 live exclusively at the TerraScale
  Platform. Tenant scoping stays engine-enforced from backend-supplied
  context (the channel binding, below).
- **QUIC with mTLS, deferred**: the networked binding for remote or
  tiered engine access. PD-2026-08-26-11 D1 defers it until a
  networked-engine need is recorded. The deferral keeps the decision
  record item: when a networked link class appears, the binding lands as
  the same MessagePack frame layer over `quinn` (Rust) and
  `System.Net.Quic` (C#) with mTLS, and the UDS-vs-QUIC same-host
  comparison becomes executable. The binding-split affirmation and the
  measured local evidence live in `docs/sync/tbep-binding-evidence.md`:
  per-frame deterministic byte accounting (measured, exact) plus a
  same-host UDS round-trip characterization (engineering evidence, not a
  public claim; PD-2026-08-26-03 claim discipline unchanged).

No database data rides NATS (PD-2026-08-26-13 D2) — TBEP is an
engine-owned socket, not a broker lane.

## The frame layer

Every frame, in both directions, is a MessagePack array of exactly:

```text
[ version u8, kind u16, correlation u64, causation u64,
  idempotency str?, digest u64, body bin ]
```

On the stream, every frame carries a `u32` little-endian length prefix
of its envelope bytes.

- **version** is `1`. Unknown versions fail closed on both sides
  (`unsupported_version`).
- **kind** is a registry code (the taxonomy below). Unknown kinds fail
  closed (`unknown_frame_kind`).
- **correlation** is this frame's identity. A client assigns its request
  identities; the server assigns its answer identities from 1 per
  connection. **causation** names the peer frame: every response echoes
  its request's correlation as causation. Unsolicited notifications
  (backbone streams) carry correlation `0` and causation equal to the
  subscription they belong to — frame identity stays deterministic
  without cross-thread coordination.
- **idempotency** rides as msgpack `nil` when absent and a bounded
  utf-8 string when present, exactly on mutation-family request frames
  (`document.write`, `document.delete`, `family.write`,
  `sync.mutation_submit`). The pairing is enforced at seal time and on
  decode, both sides: a mutation without its key and a read with one are
  both refused (`idempotency_pairing`). Keys are non-empty, at most 128
  bytes, control-free. **nil anywhere else fails closed** — the
  nil-handling pitfall the codec survey flagged (PD-2026-08-26-02 D4),
  pinned by the corpus and the adversarial suites.
- **digest** is FNV-1a 64 over the body bytes. Disagreement fails closed
  (`digest_mismatch`).
- **body** is the encoded frame body: a positional MessagePack array of
  the schema-declared fields — **never a map, never a typeless blob**
  (`bin` carries it through the envelope). Contractless/typeless codec
  modes are never enabled on either side.

### Bounds and failures

- `max_frame_bytes` = 16,777,216: a frame (declared or actual) beyond
  the bound is refused before any field parses (`frame_too_large`); on
  the stream the refusal is fatal to the connection because a hostile
  declared length leaves the stream position unrecoverable.
- Every string field carries a schema-declared byte bound; every list a
  256-item bound; the error `detail` a 256-byte bound. Encode validates
  before bytes exist; decode validates after structure.
- Truncation, trailing bytes, wrong arity, out-of-range integers,
  unknown enum tags, and structural garbage all fail closed with
  `malformed_frame`. A frame that parses but rides the wrong direction
  (an engine-to-host frame sent host-to-engine) is refused as
  `malformed_frame` ("wrong direction").
- The fail-closed behavior is proven, not asserted: the Rust adversarial
  suite (`tests/adversarial.rs`) and the C# corpus rows
  (`adversarial-rejection.json`) both replay hostile bytes, and the
  cross-language corpus pins every golden frame byte-exact in both
  directions.

### The value type

Document bodies and family/sync payload fields carry a recursive value
type: a tagged array `[tag]` (null) or `[tag, payload]` —
`0 null, 1 boolean, 2 integer (i64), 3 number (f64), 4 string,
5 array, 6 object`. Objects are `[(name, value)]` pairs carried
**name-sorted** — the canonical form — so identical values encode to
identical bytes in both languages regardless of in-memory field order.
The map/array distinction and nil placement inside values are pinned by
the `schema-pitfalls` corpus category.

## The frame taxonomy

The one source of truth is `crates/terrabase-tbep/schema/tbep-registry.json`
(schema identity `terrabase.engine.protocol/registry@1`, registry version
1.0, wire version 1). The `tbep-gen` tool
(`crates/terrabase-tbep/tools/tbep-gen`, `cargo run` there) regenerates
the committed bindings on both sides from it:

- Rust: `crates/terrabase-tbep/src/generated/registry.rs`;
- C#: `host/TerraBase.Protocol/Generated/TbepRegistry.g.cs`.

The drift contract: the committed files must equal regeneration
byte-for-byte (`tests/registry_drift.rs` re-runs the generator and
fails on any difference), and every `PB-*` parity reference in the
operation registry's **parity-mapping column** must name a row of
`parity/inventory.json` (PD-2026-08-26-14 D1, the GAP-A1 fold); every
operation carries either such references or an explicit
`new-capability` marker. The CI wiring (regeneration plus the drift
check in `.forgejo`) is an integrator action recorded with this packet.

Frame codes: request frames occupy the family blocks `0x0100`+
(documents/queries/families), `0x0110`+ (backbone), `0x0120`+ (sync
delivery), `0x0130`+ (topology/admin/replication observation),
`0x0140`+ (supervision); response
frames answer at request code + `0x100`; notifications occupy `0x0300`+;
the channel `error` frame is code `1`.

| Family | Operations (request → response codes) |
| --- | --- |
| supervision | `channel.bind` 320→576, `supervision.health` 321→577, `supervision.shutdown` 322→578 |
| documents | `document.read` 257→513, `document.write` 258→514, `document.delete` 259→515 |
| query | `query.execute` 260→516, `query.cancel` 261→517 |
| families | `family.read` 262→518, `family.write` 263→519 |
| backbone | `backbone.subscribe` 272→528, `backbone.acknowledge` 273→529, `backbone.unsubscribe` 274→530 |
| sync delivery | `sync.session_setup` 288→544, `sync.delta_pull` 289→545, `sync.snapshot_pull` 290→546, `sync.checkpoint_ack` 291→547, `sync.mutation_submit` 292→548 (mutation), `sync.session_close` 293→549 |
| topology/admin | `topology.read` 304→560, `admin.status` 305→561 |
| replication observation | `replication.conflicts` 306→562, `replication.fenced` 307→563, `replication.depositions` 308→564, `replication.cursor` 309→565, `replication.health` 310→566, `replication.denials` 311→567 |
| notifications | `subscription_batch` 769, `trigger_notification` 770, `subscription_closed` 771 |
| channel | `error` 1 |

Notable rows:

- **`query.cancel` (261)** is the GAP-Q12 fold (PD-2026-08-26-14 D3):
  the wire-side query cancellation lifecycle handoff (PB-4034, PB-1012)
  — the live signal a host sends to cancel an in-flight query, keyed by
  the query identity, mapped onto the engine's bounded-checkpoint
  cancellation seam.
- **`trigger_notification` (770)** is the content-free trigger event of
  PD-2026-08-26-13 D3: on commit publication the consumer receives a
  small typed frame carrying only the subscription, the collection, the
  sequence, and the operation id — never document data — and pulls
  delta batches from its checkpoint (push the hint, pull the delta).
  The unsolicited-notification identity rule applies (correlation 0,
  causation = subscription id).
- **`sync.mutation_submit` (292)** is the only mutation of the
  sync-delivery family: server-ack-only, the idempotency key is the
  at-least-once retry identity, and a retried key answers the recorded
  receipt (PD-2026-08-26-12 D2). No optimistic patches exist on this
  channel. The serving domain of its field values is **scalar** (the
  adopted core's `SyncValue` knows null, bool, int, float, string): the
  wire value type stays recursive (composites decode and re-encode
  byte-exact in both languages — the corpus's composite row pins it),
  and a composite field value is answered with the typed
  `invalid_body` rejection, never silently coerced. The applied-key
  registry of that retry identity is bounded: each scope retains the
  most recent 128 receipts (the checkpoint window), drop-oldest, so a
  retry older than that horizon — behind more than a full retained
  window of mutations — may re-apply. That at-least-once caveat is the
  documented cost of bounded serving memory.
- **The replication observation family (306..=311)** is the GAP-R03 fold
  (PB-5035): six content-free reads of a member's bounded observable
  facts — conflicts, fenced-leader history, depositions, cursor
  position, health, and the tenant-isolation denial log — over the
  engine's typed observation operations
  (`src/replication/observation.rs`, decision record
  `docs/replication/gapr03-replication-observations.md`). No request
  carries a tenant (the bound channel's tenant is the scope, per
  PD-2026-08-26-11 D1), no record type can carry a document body, and
  the shipped hosts answer an unserved request with the typed
  `operation_refused` until the engine-backed host integrator action
  lands.

## The channel binding and tenant isolation

PD-2026-08-26-11 D1: no TBEP frame carries a tenant. The tenant is
backend-supplied context, bound once per connection:

1. The host opens the UDS connection (same-user, `0600` socket).
2. The host sends `channel.bind` with the backend principal and tenant.
3. The engine binds the channel; every subsequent tenant-scoped
   operation is served under the bound tenant, and the engine enforces
   isolation between them.
4. A tenant-scoped frame before the binding is refused
   (`unbound_channel`); a second bind on the same connection is refused
   (`already_bound`).

The binding fails closed: the tenant binds the channel exactly when the
engine accepted the binding — a refused bind (a draining engine refuses
every new binding) leaves the channel **unbound**, so the next
tenant-scoped frame on that connection is refused as `unbound_channel`,
never served under a tenant the engine never accepted.

Tenant isolation extends over the whole session and subscription
lifecycle, engine-enforced: every sync session and backbone subscription
records the tenant of the channel that created it, and a lifecycle frame
— `sync.checkpoint_ack`, `sync.session_close`, `backbone.acknowledge`,
`backbone.unsubscribe` — that names another tenant's session or
subscription is refused with the typed refusal (`session_refused` /
`subscription_refused`, detail `bound to another channel`) and changes
nothing: the owner's channel keeps full lifecycle ownership.

There is no authentication at this boundary by design — the boundary is
local and same-user; customer authentication and authorization live
exclusively at the TerraScale Platform (PD-2026-08-26-11 D1).

## The supervision contract

- `supervision.health` answers the serving state
  (`starting | serving | draining | stopped`), the session and
  subscription counts, and the frames-served counter.
- `supervision.shutdown` begins a graceful shutdown: the answer carries
  the draining state and the in-flight count; every open backbone
  subscription receives its `subscription_closed` notification
  (reason `shutting_down`); the connection then closes cleanly. The
  process exits when the last connection ends.
- The server treats every decode refusal as fail-closed and typed: the
  error frame names the code; recoverable refusals keep the connection
  usable for the next frame; unrecoverable positions (unknown version,
  oversized declared prefix) close it. Graceful shutdown and end of
  stream close it cleanly.
- The slow-consumer policy: every outbound path of one connection —
  its answers and its subscription drainers — hands off through one
  bounded channel of 64 frames. A consumer that falls a full depth
  behind is closed out: the connection ends (never a blocked engine
  thread, never unbounded serving memory) and each subscription's
  stream stops. The same verdict the sync core's `slow-consumer`
  budget row records, re-profiled onto the engine channel.

## Bytes-on-wire budget (PD-2026-08-26-13 D1)

The written per-frame envelope-overhead budget, recorded in the schema,
the generated registries, and the corpus:

| Class | Wire-byte budget | Frames |
| --- | --- | --- |
| control | 256 | error, channel.bind, topology.read, admin.status, supervision.* |
| operation | 1024 | document.read, document.delete, query.cancel, family.*, backbone.* |
| operation_large | 65,536 | document.write, query.execute |
| subscription | 65,536 | subscription_batch |
| trigger | 256 | trigger_notification |
| delivery | 65,536 | sync.* |
| observation | 65,536 | replication.* |

The **fixed envelope cost bound** is 35 bytes over the body (the
all-maximal-width encodings: array header, u8, u16, two u64 identities,
nil idempotency, u64 digest, bin8 header) plus the 4-byte stream prefix.
Small integers ride as msgpack fixints, so actual frames sit below the
bound (a minimal control frame measures 19+4 bytes); the bound is the
worst case the budget reasons over. Both runners assert the measured
`stream bytes ≤ class budget` for every corpus frame, and the measured
per-frame overhead (`stream wire bytes − body bytes`) is recorded in
each corpus row — the numbers are deterministic byte counts, not
timings. Bytes-on-wire **per delivered change** (delta compression,
batching) is the sync core's budget (SE-04/SE-02), not TBEP's; the
delivery frames here carry the already-batched payloads.

## Conformance

- The golden corpus (`crates/terrabase-tbep/vectors/tbep/`, seven
  categories) records every frame's envelope bytes, body bytes,
  identity, and measured overhead. The Rust runner
  (`tests/vectors_runner.rs`) and the C# runner
  (`TerraBase.Protocol.Tests/TbepGoldenVectorsTests.cs`) replay it:
  decode(bytes) == frame, encode(frame) == bytes, both sides, both
  languages. Regeneration: `cargo test --test vectors_runner --
  --ignored regenerate_corpus` rewrites the corpus from the typed
  scenarios; a codec that changes any byte breaks replay — a contract
  change, not noise.
- The adversarial suites prove fail-closed behavior on both sides:
  `tests/adversarial.rs` (codec, envelope, and live-socket rows) and
  the corpus's `adversarial-rejection` category (byte-level hostile
  frames with their recorded failures, replayed by both languages).
- The UDS end-to-end suite (`tests/server_uds.rs`) drives every frame
  family the shipped host owns over a real socket: channel binding
  (including the `0600` permission, the unbound/already-bound
  refusals, and the fail-closed refused bind), tenant isolation over
  the session and subscription lifecycle (two bound channels), the
  sync-delivery family end to end (session, mutation with idempotent
  retry, delta and snapshot pulls, checkpoint acks),
  the backbone subscription streams (ordered batches then the
  content-free trigger), the topology/admin reads, the supervision
  contract, and the `query.cancel` live signal.
- The C# interleaving row (`TerraBase.Protocol.Tests/
  TbepClientInterleavingTests.cs`) pins the client's unsolicited-frame
  contract against a scripted engine over a real UDS pair: the batch
  and trigger that race an in-flight call are buffered and surface, in
  arrival order, from the next `ReadFrameAsync` — never consumed and
  discarded.

## Deployment shape and named integrator actions

The engine process hosts one `TbepServer` behind a
[`TbepHost`](../../crates/terrabase-tbep/src/host.rs) seam. Two hosts
ship:

- `SyncDeliveryHost` — the real serving half over `terrabase-sync-core`
  (the adopted base engine): sync sessions, budgeted delta and snapshot
  delivery, checkpoint acknowledgements, server-ack mutations with
  idempotent retry identities, journal-backed backbone streams with
  content-free triggers, topology/admin reads, and supervision.
- `ScriptedHost` — the conformance fixture over the real host for the
  document/query/family operation frames (deterministic scripted
  outcomes; a fixture, not an engine).

Named integrator actions recorded with this packet (shared-file or
engine-crate seams outside this packet's write scope):

1. Wire `crates/terrabase-tbep` into the root workspace `members` list
   (the crate is a standalone workspace exactly as
   `crates/terrabase-sync-core` was before its integrator wiring).
2. Wire CI (`.forgejo`): run `cargo test` from the crate directory
   (drift + corpus + adversarial + UDS suites) and
   `dotnet run` from `host/TerraBase.Protocol.Tests`.
3. The engine-backed `TbepHost`: a small public engine-side constructor
   (the Platform-adapter constructors are crate-private by design,
   W5-E) plus `impl TbepHost` mapping document/query/family requests
   onto the engine seams and the engine backbone. The seam is shaped so
   the wiring is thin.
