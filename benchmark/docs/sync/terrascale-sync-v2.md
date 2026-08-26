# The `terrascale.sync.v2` wire protocol (SE-04)

Status: implementation record for the SE-04 packet (issue #6), 2026-08-26.
Authority: charter PD-2026-08-26-05 (native sync engine),
PD-2026-08-26-10 (surface compatibility), PD-2026-08-26-12 (native
clients, server-ack mutations), PD-2026-08-26-13 (bandwidth budgets,
trigger events); ADR 0005 D-2/D-3, ADR 0008, ADR 0012; the SE wave
spec (SE-04); the SE-02 GO decision record
(`docs/spike/se02-base-engine-spike.md`). Worktree `terrabasev2-se04`,
branch `fleet/se04-2`, based on `develop` @ `c6359a9`.

This document is the wire spec of the client-channel Sync Protocol: the
versioned frame vocabulary spoken between client Sync Sessions and the
serving host. The typed implementation is
`crates/terrabase-sync-core/src/protocol/` (`terrabase_sync_core::protocol`);
the golden conformance corpus is
`crates/terrabase-sync-core/vectors/terrascale.sync.v2/` with its Rust
runner at `crates/terrabase-sync-core/tests/vectors_runner.rs`.

## Scope and boundaries

- This is the **client channel** only: SDK sync clients (TypeScript,
  C#, later languages) implement this contract natively
  (PD-2026-08-26-12 D1). The server-side delivery core is the
  `terrabase-sync-core` seam crate over Loro (SE-02 GO).
- **TBEP (SE-14) is a separate protocol.** TBEP is the C#↔Rust
  process-to-process protocol (MessagePack over UDS/QUIC, ADR 0005
  D-2); `terrascale.sync.v2` is the client Sync Protocol. What SE-14
  reuses from here is the **envelope discipline** (below), not these
  types or this binary encoding.
- The engine C ABI has no client-side consumer (PD-2026-08-26-12 D4):
  conformance, not compilation, is the compatibility guarantee, and
  this corpus is that conformance contract.

## The envelope discipline (shared with TBEP)

Every frame, in both directions, carries exactly:

| Field | Encoding | Discipline |
| --- | --- | --- |
| Version | `u8` = `2` | Unknown versions fail closed; see versioning. |
| Frame kind | `u16` | Stable registry codes; unknown kinds fail closed. |
| Correlation id | `u64` | This frame's identity; monotonic per sender. |
| Causation id | `u64` | The peer frame this one answers. |
| Idempotency key | presence `u8`, then `u64` length + utf-8 | Present **exactly** on mutation-family frames; the pairing is enforced at seal time and on the wire. |
| Content digest | `u64` | FNV-1a 64 over the canonical body bytes; a decoded frame that disagrees fails `verify()` (decode succeeds; callers reject). |
| Body length + body | `u64` length, then bytes | The canonical body begins with its own `u16` kind tag; length disagreement fails closed. |

The fixed envelope cost is **38 bytes** (36 header bytes plus the body's
kind tag): a heartbeat — the zero-field body — encodes to exactly 38
bytes, asserted by the contract tests and recorded in the vectors. All
integers are little-endian. The encoding is deterministic across runs
and platforms: identical frames produce identical bytes, which is what
makes the golden corpus possible.

TBEP reuses this discipline — version, frame type,
correlation/causation, idempotency key where mutation is possible,
content digest, fail closed — in its MessagePack profile (SE-14's
spec).

## Frame taxonomy

| Code | Kind | Direction | Carries | Class (budget) |
| --- | --- | --- | --- | --- |
| `0x0001` | `session_setup` | client → server | collections, optional resume checkpoint | session (4096) |
| `0x0002` | `session_accepted` | server → client | session id, server checkpoint, active collections | session (4096) |
| `0x0003` | `subscription_modify` | client → server | add/remove collection lists | subscription (2048) |
| `0x0004` | `subscription_changed` | server → client | the active collection set | subscription (2048) |
| `0x0005` | `checkpoint_ack` | client → server | the session's durable checkpoint | acknowledgement (1024) |
| `0x0006` | `heartbeat` | both | nothing | heartbeat (64) |
| `0x0007` | `delta_batch` | server → client | `loro-delta/1` payload, next checkpoint, budget flags, commit count | delivery (65 536) |
| `0x0008` | `snapshot_answer` | server → client | `loro-snapshot/1` payload, checkpoint | delivery (65 536) |
| `0x0009` | `trigger` | server → client | collection scope + storage sequence + operation id (content-free) | trigger (256) |
| `0x000A` | `resync_required` | server → client | retained floor checkpoint, reason | control (256) |
| `0x000B` | `region_route_change` | server → client | new serving region name | control (256) |
| `0x000C` | `error` | server → client | closed error code, bounded detail | control (256) |
| `0x000D` | `mutation_request` | client → server | collection, document, typed write + envelope idempotency key | mutation (65 536) |
| `0x000E` | `mutation_ack` | server → client | applied identities or typed rejection, echoing the key | acknowledgement (1024) |

The error-code vocabulary is closed: `unsupported_version`,
`malformed_frame`, `checkpoint_refused`, `scope_refused`,
`session_closed`, `overloaded` (wire codes 1–6). Mutation rejections
reuse this vocabulary. Resync reasons are
`behind-retained-floor` (1) and `history-compacted` (2).

## Versioning and fail-closed rules

- The wire version is the first byte: `2`. Decode rejects every other
  value with `unsupported_version` — **including `1`**: no v1 frame
  decodes, by design (below).
- Unknown frame kinds, unknown payload codecs, unknown error codes,
  unknown enum tags: rejected.
- Truncation at any length, trailing bytes, any length field that
  disagrees with the actual bytes: rejected.
- The decode bound is 16 MiB (`MAX_FRAME_BYTES`); larger inputs are
  rejected before any field is parsed.
- String, list, and field limits are enforced at seal time and on
  decode symmetrically (identifiers ≤ 256 bytes; trigger identifiers
  ≤ 64 bytes; idempotency keys and operation ids ≤ 128 bytes;
  collection lists ≤ 256 entries; mutation bodies ≤ 1024 fields).
- The idempotency pairing is a wire rule: a mutation frame without its
  key, or a non-mutation frame with one, is rejected.
- A delta batch's `single_commit_oversize` flag requires `commits == 1`
  — an oversize claim on a multi-commit batch is malformed.
- Digest disagreement is the caller's refusal (the frame decodes;
  `verify()` returns false), matching the SE-02 frame discipline.

Additive changes (new frame kinds, new error codes) within version 2
are minor-compatible: an implementation that decodes an unknown **kind**
fails closed, so additions are negotiated at session setup, and this
document's registry is the versioned record. Breaking envelope changes
bump the version byte; nothing may decode across a version boundary.

## Ordering, at-least-once, and idempotency guarantees

- **Delta batches are checkpoint-ordered.** Each batch names the
  checkpoint after applying it; a session applies batches in the order
  the server serves them from its checkpoint. The serving core is
  single-writer (the fixed server peer), so the order is the journal
  order.
- **Delivery is at-least-once.** After a reconnect from the same
  unacknowledged checkpoint, the server re-serves the identical delta
  bytes (the `reconnect-replay` vector records the byte-identical
  re-delivery). Loro import is idempotent — re-applying the same batch
  changes nothing — so duplicate delivery is safe by construction.
- **Retransmission is checkpoint-bounded.** The client's
  `checkpoint_ack` bounds what the server may need to re-send; nothing
  before the last acknowledged checkpoint is retransmitted. A session
  older than the retained floor is **never** served a stale or empty
  batch that would masquerade as currency: it is answered
  `resync_required` with the floor and recovers by adopting a full
  snapshot (the `resync-required` vector).
- **Triggers are advisory and at-most-once per push.** They carry no
  ordering role; a dropped or coalesced trigger costs a later pull,
  never correctness — the pull (`serve delta from checkpoint`) is
  authoritative.
- **Heartbeats carry no ordering and no payload**; the answer echoes
  the request's correlation id as its causation id.
- **Mutations are server-ack-only** (PD-2026-08-26-12 D2). The only
  mutation frame a client sends is `mutation_request` with an
  idempotency key; the only answer is `mutation_ack`. A retry with the
  same key is the same mutation. No frame kind carries an optimistic
  patch, a client-side delta, or any client-encoded payload: the
  server is the single writer of record (ADR 0005 D-3), and the
  mutation reaches the client's document state only through the
  journal → delta path.

## The Loro wire decision: wrapped

**Decision: wrap.** The adopted base engine's binary payloads ride
inside `terrascale.sync.v2` frames as **named, versioned payload
codecs**:

- `loro-delta/1` — a Loro update-range export (`ExportMode::
  UpdatesInRange` on the single server peer's span) rides only in
  `delta_batch`;
- `loro-snapshot/1` — a Loro snapshot export rides only in
  `snapshot_answer`.

The codec/kind pairing is enforced by the codec (a snapshot codec on a
delta body is malformed, at seal time and on decode). The payload bytes
are never re-encoded, so the wrap adds only the 38-byte envelope and
per-kind fixed fields to what Loro produces.

Why wrap, and not adopt or extend the Loro Protocol directly:

1. Loro's wire artifacts are document-delta encodings, not a session
   protocol. Session setup/resume, subscriptions, acknowledgements,
   heartbeats, region routing, resync floors, error taxonomy, version
   negotiation, and trigger notifications have no Loro equivalent;
   adopting its format as the channel would mean inventing these
   inside a foreign container format without a registry we own.
2. Extending the Loro format would fork it: every upstream change
   would collide with our extensions, and the SE-02 record's
   conditions (budgeted, inspectable hot path) would be harder to
   hold. Wrapping pins the integration surface to two named payload
   codecs; a Loro version bump is a codec-version bump
   (`loro-delta/2`) negotiated at the session layer, not a wire
   break.
3. The client-side subset stays small (PD-2026-08-26-12): SDKs
   implement the single-writer receive path (import server deltas;
   never merge local writes), which the SE-02 record already sizes as
   materially smaller than a full CRDT client.

The generic byte-blob escape hatch is exactly these two codecs and
nothing else: no other frame kind carries unstructured bytes, both
codecs are versioned identities in this registry, and both are
digest-covered. The vectors assert that no client-to-server frame
carries a payload at all.

## `v1` compatibility statement

There is **no wire compatibility** with `terrascale.sync.v1` (charter
PD-2026-08-26-10: V2 ships exactly one service surface; no V1 HTTP/TCP
wire compatibility). A v1 frame does not decode — the version byte `1`
fails closed, recorded in the `malformed-frame-rejection` vector.

The accepted v1/v2 behavioral difference is the mutation model: v2
clients are server-ack-only and drop optimistic patches
(PD-2026-08-26-12 D2); the SDK tickets record the removal. Customer
migration runs through logical migration (BK-03), never wire
compatibility. Conformance for every SDK implementation — in any
language, native — is the golden corpus below (PD-2026-08-26-12 D1/D3).

## Trigger notifications (PD-2026-08-26-13 D3)

On commit publication the serving side pushes a `trigger` frame: the
collection scope, the commit's storage sequence, and its stable
operation id — three fields, nothing else. The client answers by
pulling delta batches from its checkpoint (**push the hint, pull the
delta**). The frame:

- is **content-free** — no document identity, no field names, no
  values, no bodies; the runner asserts the trigger's structured
  description holds exactly the envelope fields plus the three
  content-free fields, so any payload-carrying extension breaks the
  corpus;
- is **small** — 77 bytes in the recorded vector; the written budget
  is 256 bytes, which bounds the worst-case identifier lengths
  (64-byte collection + 64-byte operation id + envelope = 190 bytes);
- never blocks a commit and is never a substitute for the pull: the
  authoritative state exchange is always the checkpoint-bounded delta
  path.

## Bytes-on-wire budget definitions (PD-2026-08-26-13 D1)

These are **written definitions** — the contract each frame class is
held to. Measured budgets (bytes per delivered change, per-frame
envelope overhead, trigger size) against these definitions are
SE-07/SE-15's deliverable under the benchmark-mandatory rule
(PD-2026-08-26-03); nothing in this record is a measured claim.

| Class | Frames | Written budget (wire bytes) |
| --- | --- | --- |
| delivery | `delta_batch`, `snapshot_answer` | 65 536 (the default batch byte budget) |
| trigger | `trigger` | 256 |
| heartbeat | `heartbeat` | 64 |
| acknowledgement | `checkpoint_ack`, `mutation_ack` | 1024 |
| subscription | `subscription_modify`, `subscription_changed` | 2048 |
| session | `session_setup`, `session_accepted` | 4096 |
| control | `resync_required`, `region_route_change`, `error` | 256 |
| mutation | `mutation_request` | 65 536 |

Rules the definitions carry:

- **Delta delivery, not re-sends**: repeated changes to established
  documents must ride deltas. The `repeated-change-delta` vector holds
  the invariant in CI (delta bytes < snapshot bytes for the same
  state) and records the corpus numbers an SDK can recount: 8 repeated
  changes → 497 delta wire bytes (407 payload + 90 envelope and
  fixed fields) against a 2788-byte snapshot of the same state; 62
  wire bytes per delivered change. These are deterministic corpus
  values, not measurements of a running system.
- **Per-frame envelope overhead** is defined as wire bytes minus
  payload bytes; the fixed component is 38 bytes. Every recorded
  frame's overhead is recomputed by the runner from the committed
  bytes.
- **The single exception** is a commit whose delta alone exceeds the
  delivery budget: it rides alone, flagged `single_commit_oversize`
  with `commits == 1` — never silently, never merged (the SE-02 seam
  contract; the `slow-consumer` vector exercises it).
- **Checkpoint-bounded retransmission** bounds re-sends to the
  unacknowledged window (above).

## Content rules (no credentials, no cross-tenant data, no escapes)

- No frame carries credentials, tokens, secrets, or connection
  strings. The tenant **never rides a frame**: a session is bound to
  its tenant scope at the transport/authorization layer
  (backend-supplied context; PD-2026-08-26-11), and the frame
  vocabulary has no tenant field at all.
- Cross-tenant data cannot appear in a frame: payloads are exports of
  one tenant-scope document held by one `SyncCore` instance, and the
  serving glue serves only that scope.
- The only byte payloads are the two named payload codecs (above);
  every other field is typed and bounded. Error details are bounded to
  256 bytes and are machine-readable reason text, never document data.
- Every frame is versioned and typed; the codec has no passthrough,
  no "unknown body" variant, and no generic map/array escape.

## The golden conformance corpus

Machine-readable JSON, committed at
`crates/terrabase-sync-core/vectors/terrascale.sync.v2/`, one file per
category:

`fresh-session`, `checkpoint-resume`, `reconnect-replay`,
`slow-consumer`, `region-route-change`, `resync-required`,
`malformed-frame-rejection`, `repeated-change-delta`,
`trigger-notification`.

Schema identity `terrascale.sync.v2/vector-corpus@1`. Each file
declares the server scope, core configuration (batch budget,
checkpoint window), and the initial journal; then an ordered step
list: `apply` (journal commits), `client_frame` / `server_frame`
(recorded wire bytes **and** the structured frame description),
`serve_delta` (serve from a checkpoint; expected frame bytes or null),
`serve_snapshot`, `serve_trigger`, `decode_rejects` (bytes + expected
typed rejection), `budget` (class-budget, payload, overhead, and
per-change accounting), and `note` (prose).

How conformance runs:

- **Rust (this repository, CI)**:
  `cargo test --offline --locked` in `crates/terrabase-sync-core`
  replays every file through the real `SyncCore` and protocol codec
  and asserts byte-exact frames (the corpus generation path and the
  replay path are the same codec, so this is a determinism and
  regression gate).
- **Every SDK's CI (SE-05, SE-09, SE-10)**: for each recorded frame,
  assert `decode(wire) == frame` and `encode(frame) == wire` against
  the structured description; for each `decode_rejects` entry, assert
  the typed rejection. The corpus is JSON precisely so no toolchain
  assumption crosses languages.
- **Regeneration** (deliberate codec change only):
  `cargo test --test vectors_runner -- --ignored regenerate_corpus`
  rewrites the corpus from the typed scenario definitions in the
  runner. A corpus that regenerates differently without a deliberate
  change is a contract break, not noise.

## Non-claims

- No performance, bandwidth, latency, or throughput claim is made or
  implied anywhere in this packet (PD-2026-08-26-03,
  PD-2026-08-26-13 D1/D4). The budget table holds definitions; the
  corpus numbers are deterministic artifact values an inspector
  recomputes.
- The mutation frames define the wire contract only: validation,
  journalling, and acknowledgement execution belong to the engine seam
  and the serving host (SE-07/SE-14 scope), not this crate.
- Session/subscription state machines, transport bindings, and
  endpoint re-resolution after `region_route_change` are consumers'
  concerns (SDKs, SE-14); this record fixes the wire vocabulary and
  guarantees.
- The vectors cover the frame vocabulary and its guarantees; they do
  not qualify any SDK implementation (SE-05/SE-09/SE-10 do that).
