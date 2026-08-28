# The native `terrascale.sync.v2` sync client (SE-10)

Status: implementation record for the SE-10 packet
(TerraScale/TerraScale.DotNet#9), 2026-08-27, updated by repair loop 1
(same day: typed decode bounds, live-loop resilience, and host-surface
bounds from the packet review) and by the follow-ups record below
(same day: the TerraBaseV2#89 warden MINORs and the TerraBaseV2#93
SDK half). Authority: workspace
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
  `AcknowledgeCheckpointAsync` bounds retransmission. A resync-required
  refusal (409 with the resync marker) carries the retained floor the
  host stated in its refusal body into the typed outcome (the initial
  checkpoint when the host sent none).
- `MutateAsync` is **server-ack-only** (PD-2026-08-26-12 D2): the typed
  write plus the caller's idempotency key; the answer is the applied
  receipt or the typed refusal. Nothing applies locally — the change
  reaches the session's state only through the delivery path. The
  delivery accounting counts applied outcomes only
  (`MutationsAcknowledged`); refusals carry their own counter
  (`MutationsRejected`) — a rejection is never an acknowledged
  mutation. This records the accepted v1/v2 behavioral difference: the
  v2 surface has no optimistic-patch API to remove because none was
  ever added to it; the v1 session surface in this package is
  untouched.
- `UpdatesAsync` is the trigger-driven live loop: it opens the session
  (adopting the server snapshot when fresh), subscribes to
  `/v1/streams?collections=<c>`, and answers every content-free trigger
  by pulling delta batches until current and acknowledging — **push the
  hint, pull the delta**; there is no polling (PD-2026-08-26-13 D3). A
  dropped stream reconnects with `Last-Event-ID`; a transiently refused
  stream open (503/502/504/429 — an engine blip) reconnects the same
  way instead of faulting the enumeration; a malformed trigger hint is
  content-free noise (nothing counted, the pull still runs); an ended
  session (HTTP 410) is re-opened by resuming from the durable
  checkpoint, bounded to three backed-off re-opens before the typed
  failure surfaces; a resync demand (HTTP 409 with the resync marker)
  adopts a full snapshot. The loop runs only while the caller
  enumerates it — there is no hidden background work (the SDK safety
  rule).
- The event stream is read under bounds: multi-line data joins with a
  newline, a field value strips at most one leading space (the SSE
  rule), and any field line or accumulated event past
  `MaxStreamEventBytes` (default one mebibyte, **measured in UTF-8
  bytes**, not characters) ends the stream. An oversize abort is a host
  contract violation, not a transient condition — reconnecting
  re-delivers the same event from `Last-Event-ID` — so the aborts climb
  the same bounded ladder as transient failures: at most
  `MaxOversizeEventReconnects` (three) backed-off, escalating
  reconnects, resetting only when a reconnected stream delivers a
  well-formed event, then the typed `stream-event-oversize` host
  failure surfaces — never an unbounded abort/reconnect cycle. Host
  response bodies are read under `MaxHostBodyBytes` (default 32
  mebibytes, the host-surface counterpart of the frame layer's decode
  bound); a body past it is the typed host failure, never an unbounded
  parse.
- Delivery payloads (`loro-delta/1`, `loro-snapshot/1`) are opaque at
  this layer by the wrapped-Loro decision: the codec never re-encodes
  them, and consumers hand the bytes to their document layer. The
  client-side subset is the single-writer receive path
  (PD-2026-08-26-12 D1/D3).

## Failure surfaces

`TerraScaleSyncV2HostException` carries the HTTP status, the host's
typed error code (`unauthorized`, `checkpoint-refused`,
`session-ended`, `engine-unavailable`, `stream-event-oversize`, ...),
the bounded detail, the resync marker, the retained floor a resync
refusal states, and a status-derived retryability. The resync marker
is parsed null-safely like the TS core (`sessionErrorOf`: only a
literal `true` marks the demand) — a refusal body carrying
`resyncRequired: null` is a plain typed refusal, never a decoder
crash on the marker. In the live loop the recoverable cases are
handled (reconnect, re-setup, resync); on the explicit call path
they surface to the caller. The codec's refusals are the typed
`TerraScaleSyncV2ProtocolException` everywhere — including the
checkpoint decode, whose pair count is bounded by the bytes that
remain before any allocation, and the body reader's bounds-checked
take, so a crafted frame cannot escape the typed vocabulary through
an untyped capacity or overflow failure.

## Tests

- `TerraScaleSyncV2GoldenVectorTests`: the conformance harness above.
  Expected corpus counts are derived from the corpus JSON shape in a
  pass of its own (`CountCorpusStepsAsync` runs no production code and
  never increments inside the loop it gates), with a greater-than-zero
  sanity bound — never hand-tuned literals and never recounted inside
  the checked loop (the tautological paired counters were the #89
  repair); the checkpoint pair-count and
  reader-overflow rows pin the typed fail-closed decode; and the
  digest-mismatch row pins that a flipped body byte decodes, fails
  `Verify()`, and leaves the kind and idempotency key readable.
- `TerraScaleSyncV2SessionTests`: real-HTTP integration rows against a
  loopback host serving the API host's documented sync-session and
  stream endpoints (the SDK must not reference server projects; the
  fixture speaks the served contract over a real socket). The rows
  prove: setup/resume request shapes; snapshot adoption and
  acknowledgement; trigger-driven immediate pulls with a quiet window
  proving no polling; at-least-once duplicate detection; the
  resync-required snapshot path with the host-stated floor carried
  into the pull; server-ack-only mutations (receipt returned,
  checkpoint unmoved, retried key, typed refusal, rejection never
  acknowledged); a retryable stream-open refusal reconnecting instead
  of faulting the loop; a malformed trigger hint counted nothing with
  the pull still run; multi-line data accumulation and the
  one-leading-space field-value rule; oversize events ending the
  stream (not the loop) and oversize host bodies answered with the
  typed refusal; stream drop reconnect with `Last-Event-ID`;
  ended-session resume from the durable checkpoint plus the bounded
  give-up when the host ends every session; the local gate; the typed
  conflict without the resync marker; disposal cancelling the live
  enumeration; the engine-unavailable surface; and construction-time
  validation.

## Follow-ups record: #89 warden MINORs + #93 SDK half (2026-08-27)

Branch `fleet/fups-dotnet`, base `origin/develop` `21d21aa` (the SE-10
repair-loop-1 merge). Four repairs, each pinned red-then-green where a
red state exists.

### D1 — Oversize-event reconnect escalation (#89 item 1)

**Defect.** An event past `MaxStreamEventBytes` ended the stream and
the live loop reconnected with no attempt cap: a persistently-oversize
host produced an unbounded abort/reconnect cycle (the reconnect
re-delivers the same event from `Last-Event-ID`, so the cycle can
never make progress; the backoff also restarted at the base delay on
every stream open).

**Decision (chosen alternative: the bounded ladder).** Count oversize
aborts on the same ladder as transient failures —
`MaxOversizeEventReconnects` (three) tolerated, backed-off and
escalating aborts, then the typed
`TerraScaleSyncV2HostException` (`stream-event-oversize`, the stream's
own 200 status, non-retryable) surfaces. The counter resets only when
a reconnected stream delivers a well-formed event. Rejected
alternative: refusing terminally after the first oversize event. It is
harsher than the family discipline without being safer: the TS core's
session climbs one bounded reconnect ladder for every stream-end cause
and fails terminally on exhaustion
(`TerraScale.TypeScript/src/sync/v2/session.ts:741-757`, default eight
attempts at `session.ts:300`) — it does not terminal-fail on the first
stream-end symptom. One reconnect is a cheap tolerance for a one-off
host-side fault (the landed row
`OversizeStreamEventsEndTheStreamNotTheLoop` already pinned that
tolerance), and a bounded ladder still guarantees termination with the
evidence attached.

**Evidence.** Red: `APersistentlyOversizeStreamIsGivenUpOnTyped`
failed at base with `TimeoutException` (expected
`TerraScaleSyncV2HostException`) — the infinite cycle made visible.
Green after the fix, including the stream-connection ceiling (≤ 4
opens). The status code carried on the typed failure follows the
file's existing precedent for client-side bound violations on
successful responses (`HostBodyTooLarge` raises the typed host failure
with a synthetic code on a 200 body).

### D2 — The event bound is a byte bound (#89 item 2)

**Defect.** `MaxStreamEventBytes` was documented (and named) as a byte
bound but enforced as a character count (string lengths): a multi-byte
payload could exceed the documented byte budget while passing.

**Decision (chosen alternative: enforce bytes).** Measure UTF-8 bytes:
the line reader counts the UTF-8 byte length of each decoded chunk
(the decoder emits whole UTF-16 sequences per read, so the per-chunk
count is exact) and the accumulated event counts its joined value's
bytes plus separators. Rejected alternative: correcting the doc to
code units — it would contradict the public name and weaken the
documented budget. Pathological lone surrogates (only possible if a
host splits a pair across SSE data lines) count as their three-byte
replacement encoding, erring at most two bytes conservative per
occurrence.

**Evidence.** Red: `TheStreamEventBoundIsMeasuredInUtf8Bytes` failed
at base — a 120-character, 240-byte payload passed the 200 bound,
dispatched, and drove a delta pull (the observed request was
`/v1/sync/sessions/7/delta`, not the `/v1/streams` reconnect). Green
after the fix. The reader rewrite (one accumulating buffer instead of
quadratic string concatenation) also resolved the pre-existing
load-sensitive timeout of `OversizeStreamEventsEndTheStreamNotTheLoop`
(the base run of this packet failed that row twice, ~8 s each:
ingesting the 1 MiB line was O(n²) and blew the 5 s reconnect window
under fleet load).

### D3 — The tautological golden-vector recount (#89 item 3)

**Defect.** The recount rows kept paired counters incremented together
inside the same loop (`checked++; expected++; ... assert checked ==
expected`) — an assertion that could never fail. The pattern was
instantiated in all five counting rows, not one; the same defect, the
same fix.

**Decision.** Derive each expected total first, from the corpus JSON
shape alone (`CountCorpusStepsAsync` walks files/steps and sums a
structural shape; no production code runs there), then assert the
checked count against it. **Evidence of discrimination** (a tautology
repair has no natural red state): a temporary mutation that made the
checking loop skip the `serve_trigger` entry class failed the
assertion (32 != 33), and the restored loop passed. Both runs are
recorded below.

### D4 — Null-safe `resyncRequired` (#93 SDK half)

**Defect.** `HostFailureAsync` called `GetBoolean()` on the
`resyncRequired` token: a refusal body carrying
`resyncRequired: null` (every plain typed refusal of the #93 host
shape) threw `InvalidOperationException` instead of surfacing the
typed failure.

**Decision.** Only a literal JSON `true` sets the marker — the TS
core's parse (`TerraScale.TypeScript/src/sync/v2/session.ts:844-857`,
`resyncRequired = body.resyncRequired === true` at line 853). Red:
`ANullResyncMarkerSurfacesTheTypedRefusal` scripted a 410
`session-closed` with `resyncRequired: null` and failed at base with
`InvalidOperationException`; green after, surfacing the typed failure
(410, `session-closed`, `ResyncRequired` false). Non-claim: `code` and
`detail` keep their `GetString()` reads — a non-string `code`/`detail`
is not a recorded host shape and stays out of this ticket's scope.

### Receipts (commands and exit codes)

- `dotnet test tests/TerraScale.DotNet.Tests/TerraScale.DotNet.Tests.csproj`
  — base, run 1: exit 2 (153 total, 108 passed, 1 failed, 44 skipped;
  failed `OversizeStreamEventsEndTheStreamNotTheLoop`, TimeoutException).
- Same, base, run 2: exit 2 (identical — persistent under load, not a
  one-off; diagnosed as the quadratic reader).
- Same, red state (three new rows): exit 2 (156 total, 108 passed,
  4 failed — the three new rows red for their pinned reasons, plus the
  pre-existing timeout row).
- `dotnet build -m src/TerraScale.DotNet/TerraScale.DotNet.csproj`:
  exit 0, 0 errors.
- Same test command, green: exit 0 (156 total, 112 passed, 0 failed,
  44 skipped — the three new rows plus the formerly timing-failed row).
- Mutation run (D3 discrimination): exit 2 (1 failed:
  `EveryRecordedFrameEncodesByteExactAndDecodesBack`, "Expected to be
  33 but found 32"), then restored: exit 0 (156/112/0/44).
- Final run at HEAD (`5a39235`): exit 0 (156/112/0/44), and
  `dotnet build -m tests/TerraScale.EntityFrameworkCore.Tests/...csproj`
  (the dependent project): exit 0, 0 errors.
- Collision probe (see below): merge of `835f799` into this branch,
  guard resolved to this side, full suite exit 0 (158/114/0/44).
- Baseline comparison: the SE-10 merge recorded 110/0/48; this base
  (`21d21aa`) totals 153 with 44 environment-gated skips — the
  difference is landed develop history between the two measurements,
  not this packet. This packet's delta against its own measured base
  is +3 rows and the timing row now green.

### Deferred (owners)

- **Parent ratification (PROPOSED):** the terminal code
  `stream-event-oversize` on `TerraScaleSyncV2HttpException` follows
  the file's `host-failure` precedent rather than adding a new public
  exception type; if the warden prefers a dedicated public bound
  exception, it is a mechanical follow-up.
- **Parent (ticket-sized):** the .NET live loop still reconnects a
  plain dropped stream without any attempt cap (the TS core bounds all
  stream reconnects at eight, `session.ts:300,753-757`). This packet
  bounded the oversize path only, per ticket scope; aligning the
  dropped-stream ladder (and TS's stability-window reset) with the TS
  core is a separate behavioral decision.
- **Parent (informational):** an oversize-abort cycle resets its
  ladder when a reconnected stream delivers any well-formed event; a
  host interleaving good events with oversize ones therefore never
  exhausts the ladder (it still makes progress each cycle). The TS
  core's time-based stability window is the stricter alternative.

### Collision disclosure (PROPOSED action: parent picks one #93 guard at merge)

The #93 SDK half was double-dispatched: branch
`fleet/tbv2-x93-dotnet` (worktree `tbv2-x93-dotnet`, commits
`98eb1a3`/`835f799`) independently landed the same one-site repair
while this packet was in flight. The guards are behaviorally
identical for every JSON value kind — theirs accepts
`ValueKind is True or False` then `GetBoolean()`, this packet accepts
`ValueKind == True` then sets the marker — and their two test rows
(a plain-refusal knob appending `resyncRequired: null` to every plain
refusal body, plus a live-loop reopen row) complement this packet's
exact-shape row (410 `session-closed` with the null marker). Merge
evidence (throwaway probe worktree, discarded afterwards): merging
`835f799` into this branch conflicts textually in
`TerraScaleSyncV2Client.cs` and this record, and auto-merges the
session tests; resolving the guard to this packet's side, the
combined suite is fully green — 158 total, 114 passed, 0 failed,
44 skipped (exit 0). Either guard satisfies both test sets; the
parent should merge one packet and rebase the other's unique rows, or
cherry-pick the complementary tests.

### Non-claims

- No performance claim is made for the linear reader; the quadratic
  concatenation was removed because the bound's own detectability
  depended on it, and the observed effect is recorded as a timing
  repair, not a benchmark.
- The #93 host half (what the host actually sends) belongs to the
  other session's packet; this record only fixes the SDK's parse of
  the recorded shape.
- The vendored corpus bytes are untouched (the manifest gate still
  verifies every digest).

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
