# The native `terrascale.sync.v2` sync client (SE-10)

Status: implementation record for the SE-10 packet
(TerraScale/TerraScale.DotNet#9), 2026-08-27. Authority: workspace
charter PD-2026-08-26-12 (native-language SDK clients, no wasm/C-ABI
embeddings, server-ack-only mutations), PD-2026-08-26-13 D3
(trigger-driven delta pulls), PD-2026-08-26-11 D1 (the API host's local
gate); the SE-04 wire spec
(`TerraBaseV2/docs/sync/terrascale-sync-v2.md`), the SE-05 conformance
kit contract (`TerraBaseV2/docs/sync/terrascale-sync-v2-conformance.md`),
and the SE-08 API host (`TerraBaseV2/host/TerraBase.Api`). Worktree
`tbv2-se10`, branch `fleet/se10-2`.

Note on ticket history: the tracker body predates charter decision
PD-2026-08-26-12 and describes a C-ABI/RID-native embedding. The spec
section for SE-10 and the charter supersede it: this client is a
**native managed implementation**. There is no native asset, no interop
boundary, and nothing to package per RID.

## What this builds

- **The managed frame codec** (`TerraScaleSyncV2Protocol.cs`): the
  complete `terrascale.sync.v2` frame vocabulary — the 38-byte envelope
  discipline (version, kind, correlation, causation, idempotency
  presence, FNV-1a 64 content digest, length-prefixed canonical body),
  all 14 frame kinds, the versioned checkpoint encoding, the typed
  scalar value model, the closed error/resync vocabularies, the written
  per-class budget definitions, and the fail-closed decode rules
  (unknown versions — including v1 — unknown kinds, truncation, trailing
  bytes, length disagreement, limit violations, idempotency-pairing
  violations). Digest disagreement is the caller's `Verify()` refusal,
  matching the engine codec's discipline.
- **The client receive state** (`TerraScaleSyncV2ClientState.cs`): the
  durable resumption checkpoint plus the single-writer receive path —
  checkpoint-ordered delta application, at-least-once duplicate
  detection (a re-served batch that does not advance the position is a
  counted duplicate, never a re-application), full-snapshot adoption on
  resync, and the delivery/trigger accounting.
- **The session surface over the API host**
  (`TerraScaleSyncV2Client.cs`): setup (fresh or checkpoint-resumed),
  delta pulls with typed outcomes (delta / up-to-date /
  resync-required), snapshot pulls, checkpoint acknowledgement,
  server-ack-only mutations, close, and the trigger-driven live loop.

## The vendored conformance corpus

The golden vectors are vendored at
`tests/TerraScale.DotNet.Tests/vectors/terrascale.sync.v2/` together
with the SE-05 corpus-integrity manifest (`manifest.json`). The corpus
is the current one at vendoring time, including SE-07's regenerated
`slow-consumer.json` (a later engine-side regeneration landed while
this packet was in flight; the vendored copy was refreshed to it and
the harness re-ran green — the codec's byte-exactness held, so the
regeneration changed the corpus scenario, not the wire contract). The
manifest gate below proves the tested bytes:
`slow-consumer.json` SHA-256
`bc97fb745f860bfc2defb6e8ce0877f900261f2260f35c9e9fd14e1972641b80`,
60254 bytes.
The tests gate on the manifest before running: category coverage is
exact (nine files), every digest and size must match, and a file
without a manifest entry (or the reverse) fails the run.

`TerraScaleSyncV2GoldenVectorTests` is this SDK's native harness
(PD-2026-08-26-12 D1/D4: conformance, not compilation, is the
compatibility guarantee):

- every recorded frame: the structured description seals and encodes
  byte-exact to the recorded wire, and the wire decodes back with its
  kind, digest verification, and envelope identity intact;
- every `decode_rejects` entry: the exact typed rejection name;
- every `budget` step: class, budget fit (with the single-commit
  oversize exception), payload/overhead accounting, per-change
  accounting, and delta-below-snapshot, all recounted from the
  committed wire bytes;
- every `serve_*` expectation: the served wire decodes byte-exact and
  matches its structured description (the client-side reading of the
  serve steps; the delivery core itself is the engine's);
- the trigger content-free key set and the 256-byte written budget;
- the fixed envelope: a heartbeat encodes to exactly 38 bytes;
- the idempotency pairing rule on both seal and decode.

## The session surface

`TerraScaleSyncV2Client` (caller-owned `HttpClient`, never disposed by
the SDK; thread-safe; constructable once, reusable as a singleton)
creates `TerraScaleSyncV2Session`s. The client options carry the
backend-supplied context headers (`X-TB-Principal`, `X-TB-Tenant`) the
host's local gate requires — there is no authentication surface on this
boundary (PD-2026-08-26-11 D1), and options missing either fail at
construction with an actionable error rather than a silent 401.

- `SetupAsync` posts `/v1/sync/sessions` with the collection (and, when
  resuming, the durable checkpoint bytes) and records the server
  checkpoint and retained floor.
- `PullDeltaAsync` posts the delta pull from the session's checkpoint
  and applies the answer; `PullSnapshotAsync` adopts a full state;
  `AcknowledgeCheckpointAsync` bounds retransmission.
- `MutateAsync` is **server-ack-only** (PD-2026-08-26-12 D2): the typed
  write plus the caller's idempotency key; the answer is the applied
  receipt or the typed refusal. Nothing applies locally — the change
  reaches the session's state only through the delivery path. This
  records the accepted v1/v2 behavioral difference: the v2 surface has
  no optimistic-patch API to remove because none was ever added to it;
  the v1 session surface in this package is untouched.
- `UpdatesAsync` is the trigger-driven live loop: it opens the session
  (adopting the server snapshot when fresh), subscribes to
  `/v1/streams?collections=<c>`, and answers every content-free trigger
  by pulling delta batches until current and acknowledging — **push the
  hint, pull the delta**; there is no polling (PD-2026-08-26-13 D3). A
  dropped stream reconnects with `Last-Event-ID`; an ended session
  (HTTP 410) is re-opened by resuming from the durable checkpoint; a
  resync demand (HTTP 409 with the resync marker) adopts a full
  snapshot. The loop runs only while the caller enumerates it — there
  is no hidden background work (the SDK safety rule).
- Delivery payloads (`loro-delta/1`, `loro-snapshot/1`) are opaque at
  this layer by the wrapped-Loro decision: the codec never re-encodes
  them, and consumers hand the bytes to their document layer. The
  client-side subset is the single-writer receive path
  (PD-2026-08-26-12 D1/D3).

## Failure surfaces

`TerraScaleSyncV2HostException` carries the HTTP status, the host's
typed error code (`unauthorized`, `checkpoint-refused`,
`session-ended`, `engine-unavailable`, ...), the bounded detail, the
resync marker, and a status-derived retryability. In the live loop the
recoverable cases are handled (reconnect, re-setup, resync); on the
explicit call path they surface to the caller.

## Tests

- `TerraScaleSyncV2GoldenVectorTests`: the conformance harness above.
- `TerraScaleSyncV2SessionTests`: real-HTTP integration rows against a
  loopback host serving the API host's documented sync-session and
  stream endpoints (the SDK must not reference server projects; the
  fixture speaks the served contract over a real socket). The rows
  prove: setup/resume request shapes; snapshot adoption and
  acknowledgement; trigger-driven immediate pulls with a quiet window
  proving no polling; at-least-once duplicate detection; the
  resync-required snapshot path; server-ack-only mutations (receipt
  returned, checkpoint unmoved, retried key, typed refusal); stream
  drop reconnect with `Last-Event-ID`; ended-session resume from the
  durable checkpoint; the local gate; the typed conflict without the
  resync marker; disposal cancelling the live enumeration; the
  engine-unavailable surface; and construction-time validation.

A live API host is not exercised here: the TerraBaseV2 host is a
read-only sibling repo and this package must not reference it. The
fixture implements the host's documented HTTP contract exactly (the
SE-08 `TypedApi`/`Dto` surface), which is the same approach SE-08's own
tests take toward the engine.

## Non-claims

- No performance, bandwidth, latency, or throughput claim is made or
  implied (PD-2026-08-26-03). The budget numbers are the wire spec's
  written definitions; the corpus numbers are deterministic artifact
  values the tests recount.
- The frame codec implements the client channel's wire contract; the
  serving core, journal, and payload semantics belong to the engine.
- No native asset exists to package: the package targets `net10.0`
  only, and a consumer on another framework fails at package
  resolution with the standard NuGet error — the explicit, non-divergent
  failure the ticket row asks for.
