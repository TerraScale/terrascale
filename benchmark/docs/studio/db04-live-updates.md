# DB-04 — Live updates: SSE views driven by the event backbone via the API host

Ticket: TerraScale/TerraBaseV2#21 (DB-04, milestone M3 Live). Worktree
`.worktrees/terrabasev2-db04`, branch `fleet/db04-2`, base `da0df43`.
Status: implemented; repair loop 1 closed the warden's drop-marker
findings (section at the end); receipts below.

## What landed

The studio's dogfooding Live Views over the C# API host's backbone
subscription endpoint (`GET /v1/streams`, host/TerraBase.Api
`TypedApi.StreamSseAsync` over `BackboneHub`, fed by the SE-03 backbone):

- **The changes subscription in the typed client.** The studio contract
  (`studio/contract/terrabase-api.contract.json`) gains the real-host
  stream surface as a first-class `streams` entry — named SSE events
  (`opened`/`batch`/`trigger`/`closed`/`drop`), the id line as the last
  served sequence, `?from=` inclusive replay, `Last-Event-ID` exclusive
  resume, the backend-context gate headers, the collections filter, and
  the closed change-kind vocabulary. The generator emits
  `TerrabaseStudioClient.subscribeChanges` — a **fetch-stream** SSE
  reader (not `EventSource`: the host's local gate requires
  `X-TB-Principal`/`X-TB-Tenant` headers EventSource cannot send), with
  checkpoint-resumed reconnect, at-least-once sequence dedup, in-band
  drop markers that rewind the resume boundary, fail-closed unknown
  events, and the `pause()`/`resume()` slow-consumer control.
- **The live recent-changes feed** (`studio/src/components/live/`
  `ChangesFeed.tsx` + the pure `changes-model.ts`): renders every kind
  of the backbone's closed selector vocabulary — documents (including
  the wire `put`/`delete` forms), indexes, counters, time series,
  operations, database-level changes, conflicts — with filters mapping
  the V1 Changes selector families, a collection prefix filter, bounded
  newest-first list, and honest counters (events, replayed, duplicates,
  rejected, reconnects, drops, restricted).
- **Conflict content restrictions, structurally.** A conflict event
  renders exactly the `ConflictRecord` metadata projection — tenant,
  operations, members, clocks, outcome
  (src/replication/mod.rs:914-933) — through an allowlist with no
  body-bearing field; a conflict event claiming collection/document
  identity, a non-conflict event carrying a conflict record, or an
  unknown kind fails closed and is counted, never rendered.
- **Live status counters** on the home page
  (`LiveStatusCounters.tsx`): event-driven counts by selector kind,
  last sequence, drops — no polling anywhere.
- **Live document rows** (`DocumentListView.tsx` with `live` config):
  the documents page subscribes to its collection's stream and refreshes
  the loaded page windows through the typed client on matching events —
  event-driven only (no timer exists on the page); the live state,
  refresh count, and host-shed drops render in the panel.
- **The fixture mirrors the real host route** (`/v1/streams` in
  `scripts/fixture-server.mjs`): same vocabulary, same gate, same resume
  rules, plus deterministic controls (emit any selector kind, hold +
  shed with an in-band drop marker, re-deliver served identities, an
  unknown event name, connection drops). Typed `document.write` and
  counter/time-series `family.write` publish content-free events on the
  fixture backbone. The DB-03 documents fixture wrapper forwards the
  core's stream so the live-rows flow runs against real typed writes.
- **The real-host probe** (`studio/scripts/real-host-probe.mjs`,
  `vp run probe:real-host`): the SE-11 EngineHarness pattern in Node —
  the real engine TBEP server (`crates/terrabase-tbep`
  `SyncDeliveryHost`, throwaway cargo project cached outside the
  repository), the real `TerraBase.Api` process over loopback, and the
  REAL generated studio client (loaded via Node type stripping).
- **Tests**: `tests/changes-model.test.mjs` (16 rows: vocabulary,
  selector parity, conflict restriction, bounded feed, dedup, drop
  notice), `tests/changes-subscription.test.mjs` (8 rows against the
  fixture: every kind, collections filter, reconnect/backlog/no-dupes,
  at-least-once redelivery, drop-marker rewind, fail-closed, the gate,
  pause/resume), `e2e/live-changes.e2e.ts` (3 rows: all kinds +
  filters + conflict restrictions, in-band drop surfacing, same-session
  live row update), all wired into `vp run unit:test` (which now also
  enumerates the DB-03/05/06 test files — resolving their collision
  notes, `vite.config.ts` being this packet's scope).

## Decision records

### D-1 (the ticket's own decision): plain SSE handling, not the TS SDK's sync core

**Decision: plain SSE handling in the typed studio client.** The
wire contract is the same typed surface either way (the wave spec's
assumption); the choice is where client state lives.

- **Alternatives considered.**
  1. *Adopt SE-09's TS SDK sync core (wasm)* for client state: the
     embedded core owns document materialization per collection — the
     `terrascale.sync.v2` session surface. The recent-changes feed,
     however, needs the backbone's selector vocabulary across ALL kinds
     plus conflict records — observatory data the sync session does not
     carry (sync sessions are per-collection document delivery; the
     sync-core path would still need a second subscription for the
     feed, and SE-09's artifact lives in the sibling TerraScale.TypeScript
     checkout's build cadence).
  2. *Plain SSE handling in the generated client* (chosen): the studio
     already owns the DB-02 SSE discipline (checkpoint resume, replay
     classification, counted rejections) — extending the generated
     client keeps one typed surface, no wasm embedding, no cross-repo
     build dependency, and the whole path is testable against the real
     host (the probe) and the fixture (unit + e2e).
- **Evidence**: the probe's `live-delivery` and `checkpoint-resume`
  scenarios run the actual `subscribeChanges` client against the real
  host and pass; the fixture tests cover every kind and the marker
  paths. The sync session surface's per-collection scope is visible at
  host/TerraBase.Api/TypedApi.cs:68-74 (the `/v1/sync/sessions` routes
  are collection-scoped) versus the stream's whole-tenant scope.
- **PROPOSED for parent ratification** (the ticket records it; the
  parent owns the wave-level call). If richer client state (offline
  caches, per-document materialization) becomes a studio requirement,
  the revisit point is DB-08's parity sweep.

### D-2: fetch-stream SSE, not EventSource

The real host's gate (`TypedApi.Gate`, host/TerraBase.Api/TypedApi.cs
:976-1000) requires backend-context headers on every request including
`/v1/streams`; `EventSource` cannot send headers. The changes
subscription is therefore a `fetch` + `ReadableStream` reader with
explicit reconnect control (the tick feed keeps `EventSource` — its
pre-landing contract route needs no headers). CORS: the fixture serves
the allowlist; the real host serves none — see collision action CA-2.

### D-3: kind filters are client-side (this iteration)

The host's stream filter is collections-only
(`BackboneSubscribeAsync(subscriptionId, collections, fromSequence)`,
host/TerraBase.Protocol/TbepChannel.cs:180) — the engine backbone's
`ChangeFilter` kinds (src/streaming/mod.rs:1009-1016) do not cross the
host seam. The feed's kind filters therefore select client-side over
the rendered feed (every kind still arrives on the wire), and the
collection filter rides the server-side `collections` query. The
server-side kind carriage is collision action CA-1.

### D-4: replay classification — the first batch of a resumed connection

The `/v1/streams` surface carries no replay high-water marker (unlike
the pre-landing contract's `subscription.established.lastSeq`), and the
real host serves exactly ONE replay batch at subscribe
(`SyncDeliveryHost::backbone_subscribe` replays the retained window in
one frame, crates/terrabase-tbep/src/host.rs:693-755). The client
therefore classifies the first `batch` after a connection that opened
with `from` as the replayed backlog; every later batch is live. This is
exact on the real host and mirrored by the fixture; the probe's
`checkpoint-resume` scenario asserts it (3 redelivered, 3 classified
replayed, no duplicates).

### D-5: live rows refresh loaded windows on events (no per-row patching)

Backbone events are content-free (identities only — the engine's design,
PD-2026-08-26-13 D3; asserted in the probe's `contentFree`). Row
*contents* cannot be patched from events; the island therefore re-reads
the loaded page windows through the typed `query.execute` client on
matching events (debounced 250 ms to coalesce bursts). This is
event-driven only — no interval exists anywhere on the page (the
e2e asserts the update happens in the same browsing session with no
reload).

### D-6: `unit:test` enumerates every landed studio test file

The DB-03/DB-05/DB-06 packets authored test files they could not wire
into `vp run unit:test` (their write scopes excluded `vite.config.ts`;
each recorded a collision action). `studio/` is this packet's scope, so
the task now enumerates all ten files — the gate grew from 88 to 112
unit rows. Informational, not a collision: the integrator should keep
the list appended on future packets (or move to a glob when the task
runner supports it).

## V1 citations (parity anchors, verified at pin fa99b925)

The parity anchor is the backbone's selector vocabulary, not V1's
Studio UI. The V1 Changes client API selector surface
(`src/TerraBase.Client/Documents/Changes/` in the pinned clone):

- `IDocumentChanges.cs:10` `ForDocument`, `:20` `ForAllDocuments`,
  `:25` `ForDocumentsStartingWith`, `:30` `ForDocumentsInCollection` —
  the feed's Documents selector group + collection filter.
- `IIndexChanges.cs:8` `ForIndex`, `:13` `ForAllIndexes` — the Indexes
  group.
- `ICounterChanges.cs:10` `ForAllCounters`, `:15` `ForCounter` — the
  Counters group.
- `ITimeSeriesChanges.cs:10` `ForAllTimeSeries`, `:15` `ForTimeSeries` —
  the Time series group.
- `IOperationChanges.cs:9` `ForOperationId`, `:15` `ForAllOperations` —
  the Operations group.
- `IDatabaseChanges.cs:5-20` — the composed surface (document, index,
  counter, time-series, operation selectors), mirrored by the feed's
  filter set composition.
- The conflict selector (V1 `DocumentChangeTypes.Conflict`) maps to the
  backbone's `ChangeKind::Conflict` — src/streaming/mod.rs:975-979 and
  the SE-03 mapping table (docs/streaming/se03-change-backbone.md
  :56-72, including the recorded limit that conflict events carry no
  collection/document identity — mirrored verbatim by the feed's
  collection-filter behavior and the model's fail-closed rules).

## Receipts

All commands run in the packet worktree; exit codes recorded exactly.

| Command | Exit | Note |
| --- | --- | --- |
| `vp install --frozen-lockfile` | 0 | worktree bootstrap |
| `node --test tests/changes-model.test.mjs` (first run) | 1 | RED for the right reason: `ERR_MODULE_NOT_FOUND` — the model did not exist |
| `node --test tests/changes-subscription.test.mjs` (first run) | 1 | RED for the right reason: `client.subscribeChanges is not a function` |
| `node --test tests/changes-model.test.mjs` | 0 | 16/16 pass |
| `node --test tests/changes-subscription.test.mjs` | 0 | 8/8 pass |
| `vp run unit:test` | 0 | 112 pass, 0 fail (88 baseline + 24 new) |
| `vp check --fix` then `vp check` | 0 | formatting + lint clean (112 files formatted, 104 linted) |
| `vp run playwright:e2e` (first full run) | 1 | 6 failed: the DB-02/DB-03 no-browser-traffic assertions met the designed live subscription, the tick counter locator turned strict-mode-ambiguous, and the DB-03 wrapper lacked the stream route + gate-header CORS |
| `vp run playwright:e2e` (final) | 0 | 46/46 pass |
| `node scripts/real-host-probe.mjs` (run 1) | 0 | `"pass": true` — scenario facts below |
| `node scripts/real-host-probe.mjs` (run 2) | 1 | failed during the concurrent full-cargo-gate run on this host (died after the host build; stderr unrecorded) — disclosed as a load-time failure, re-run on the idle host |
| `node scripts/real-host-probe.mjs` (run 3) | 0 | `"pass": true` again — all scenarios (31,703 frames shed vs run 1's 31,893) |
| `cargo fmt --all -- --check` | 0 | no Rust changes; gate green |
| `cargo check --workspace --locked` | 0 | 39.8 s |
| `cargo test --workspace --locked` | 0 | dev profile; all workspace suites green |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` | 0 | 34.5 s |
| `TERRABASE_V1_ROOT=… ./scripts/check.sh` (run 1) | 1 | every cargo/parity/secret gate before the citation audit passed; the audit itself could not resolve the workspace root — the script's `../`-walk heuristic does not reach past a NESTED worktree (`.worktrees/<name>/..`) |
| `TERRABASE_V1_ROOT=… TERRASCALE_WORKSPACE_ROOT=… ./scripts/check.sh` (run 2) | 0 | "TerraBaseV2 foundation checks passed"; `cargo-deny` recorded unrun by the script's own fallback (unavailable on this host) |

### Real-host probe facts (final run, receipt excerpt)

- `live-delivery`: **pass** — 5 sync-session writes landed as monotonic,
  content-free `put` batches on the studio subscription.
- `slow-consumer`: **pass** — the stalled SSE consumer (4 KiB pinned
  receive window, no data listener) caused **31,893 frames shed** at the
  host's bounded queue while **all 24,000 flood writes were accepted**,
  `/health` served throughout, and the fast studio consumer received
  every event (24,005); delivery then recovered by checkpoint reconnect
  (128 events on the fresh connection). A blocked host could not have
  accepted the flood.
- `checkpoint-resume`: **pass** — checkpoint 24,008; exactly the 3
  missed events re-delivered with their original identities, all
  classified replayed, no duplicates.
- Iterations recorded honestly: reaching a true shed took sizing the
  flood past the sender's kernel buffers (400/2,500/4,000 events were
  absorbed silently); the first probe design gated on an in-band drop
  marker reaching the resumed stalled consumer, which Kestrel's
  response minimum data rate can abort first (see limits).

## Proposed collision actions

1. **Carry the backbone's selector vocabulary over the host seam**
   (owner: protocol + host integrator). The TBEP wire's `change_kind`
   carries `put`/`delete` only
   (crates/terrabase-tbep/schema/tbep-registry.json:292-299; the
   generated C# DTO mirrors it) and the SSE `events[]` projection has
   no conflict field (host/TerraBase.Api/TypedApi.cs:572-589) — the
   engine backbone's index/counter/time-series/operation/database/
   conflict kinds (src/streaming/mod.rs:960-980) and the
   content-free `ConflictRecord` carriage do not cross. Proposal:
   extend the registry's `change_kind`/`change_event` (a `conflict`
   variant carrying the record's metadata fields) and the host's SSE
   projection. Until then the studio renders the full vocabulary
   through the fixture, and the kinds the wire actually carries against
   the real host.
2. **CORS for browser consumers** (owner: host owner / DB-08). The real
   host serves no CORS headers; a studio served from a different origin
   cannot subscribe from the browser (the fixture serves the allowlist;
   the probe, running Node, is unaffected). Either the host allows the
   local studio origin on `/v1/streams`, or DB-08's packaging decision
   serves the studio from the host's own origin (which moots it).
3. **Frame digests on `/v1/streams`** (owner: host owner, PROPOSED).
   The pre-landing contract's envelope discipline includes a content
   digest per frame; the real host's SSE frames carry none. The client
   validates closed sets (event names, kinds, sequence monotonicity)
   but not content digests on this surface. A digest line would restore
   parity with the SE-04 discipline.
4. **`vite.config.ts` unit-test enumeration is now the integrator's to
   maintain** (informational): D-6 above; future packets append their
   test files (or the task moves to a glob when supported).

## Deferred items

- **Rich kinds + conflicts against the real host** (owner: integrator,
  after CA-1): the feed's coverage of index/counter/time-series/
  operation/database/conflict kinds against the REAL host is blocked on
  the wire carriage; fixture coverage is complete.
- **WebSocket transport** (`/v1/streams/ws`) for the studio (owner:
  DB-08): SSE-first per the wave spec; the host already serves the WS
  projection of the same frames.
- **Packaging/origin story** (owner: DB-08): how the studio ships
  relative to the host decides CA-2.
- **Live refresh of the home page's server-rendered status snapshot**
  (owner: DB-08 sweep): the typed `engine.status` read remains
  per-request RSC; wiring it to trigger notifications is a natural
  follow-up once CA-1 lands (triggers carry the sequence hints).

## Disclosed limits

- The TBEP wire today carries `put`/`delete` document events; against
  the real host the feed renders those (plus triggers, closes, drops).
  The full selector vocabulary is fixture-proven (see CA-1).
- The host's replay batch is bounded (`MAX_LIST_ITEMS` = 256,
  crates/terrabase-tbep schema `max_list_items`): a checkpoint gap
  wider than the bound re-delivers the oldest slice and needs repeated
  resumes; the probe's shed range (~24k events) hit exactly this and
  the probe asserts the exact-backlog case on a 3-event gap instead.
- Kestrel's response minimum data rate can abort a long-stalled SSE
  response before the in-band drop marker drains — the marker path
  itself is proven over the real `BackboneStream` by the host's own
  test (host/TerraBase.Api.Tests/StreamsTests.cs:185-226,
  `AFullBoundedQueueDropsCountsAndSurfaces`), the client's marker
  handling by the fixture tests, and the probe records the abort
  honestly in its receipt notes.
- The changes client's staleness watchdog is DISABLED by default: the
  `/v1/streams` surface has no heartbeat, so an idle stream is
  indistinguishable in-band from a dead one; only transport errors (or
  the opt-in `staleMs`) drive reconnects. Repair loop 1 closed the
  connect half of this gap: `connectTimeoutMs` (default 15 000 ms)
  aborts a hung connect into the reconnect path — mid-stream silence
  without `staleMs` remains by design.
- The feed's rendered list is bounded (newest 200 identities); the
  counters are the full accounting.

## Non-claims

- No performance, latency, throughput, or bandwidth claim anywhere in
  this packet (PD-2026-08-26-03, PD-2026-08-26-13); the probe is
  correctness evidence only — its timing numbers are mechanics, not
  benchmarks.
- No claim that the real host emits kinds beyond `put`/`delete` today.
- No claim of completeness across a host shed beyond the surfaced drop
  accounting and the checkpoint rewind (the UI states the boundary;
  the client rewinds to it).
- No adoption of, or claim about, the TS SDK's sync core (D-1); the
  studio adds no client-side document materialization.
- No V1 Studio UI parity claim — the parity anchor is the Changes
  client API's selector vocabulary (per the ticket); V1 Studio's page
  inventory belongs to DB-08.
- Builders did not touch `parity/**` or flip any parity row; the
  overlays' disposition of PB-6002 (the adapter carriage named above)
  stays with WS-PARITY.

## Repair loop 1 (warden FAIL: drop-marker recovery)

The warden FAILED the packet on quality, not honesty: every receipt
reproduced, but the ticket's central guarantee — after a host-shed drop
the resume path re-delivers the missed range — was nullified by the
dedup watermark. Findings → fixes → tests → receipts below.

### Findings → fixes

- **MAJOR (silent event loss, three variants).** The drop marker rewound
  the checkpoint but not the dedup watermark: (a) *checkpoint-ahead-of
  gap* — batches 1,2 and 5,6 delivered while 3,4 were shed; the
  reconnect from boundary 3 re-delivered 3,4,5,6 and the watermark
  (already 6) swallowed ALL of them as "duplicates"; (b) *surviving
  trigger* — the host's per-commit batch-then-trigger ordering lets a
  trigger survive its own shed batch, and the client let the trigger's
  sequence advance the watermark; (c) *post-marker cursor* — any batch
  cursor at or past the rewound checkpoint erased the rewind,
  contradicting the host's own marker discipline
  (host/TerraBase.Api/BackboneHub.cs `Dropped`: "a checkpoint at or
  past it cannot be trusted for the gap"). Fix: dedup after a marker
  runs on a **bounded, merged identity ledger** (surfaced-sequence
  ranges, capped at 128 with oldest-first shedding — an overflow may
  re-surface an ancient duplicate, never lose an event) instead of the
  rewound watermark; the watermark re-anchors at `resumeFrom - 1` and
  the ledger answers which re-delivered identities were genuinely
  accepted; **triggers never advance the watermark** (a push hint is
  not an accepted identity); the rewind is **sticky** — post-marker
  cursors are ignored while the gap is open, and the checkpoint
  unfreezes only when the gap is covered (the pre-marker high water is
  reached, or the resumed connection's replay batch — the host's
  single re-delivery — is consumed). `engine-generation-lost` follows
  the same discipline. All in the generator template
  (`studio/scripts/generate-client.mjs`), re-emitted into
  `studio/src/api/generated/client.ts`.
- **MAJOR (non-discriminating tests).** The drop-rewind test held one
  queued event that was also the newest, so disabling the rewind left
  8/8 green (warden mutation 1); the client's per-event batch
  validation was equally unpinned (mutation 4). Fix: seven hostile-SSE
  regression rows (below) drive the REAL generated client against
  scripted shed shapes the fixture cannot produce; both mutations now
  fail the suite (13/1 and 14/1 recorded below).
- **MINOR (generated client did not typecheck).** The generator emitted
  `StreamChangeKind` twice (contract types section + changes-stream
  section → TS2300), the `StreamChangeEvent` cast needed an `unknown`
  bridge (TS2352 at client.ts:841), and the e2e port destructure
  re-called `server.address()` after the null/string guard (TS2339 at
  live-changes.e2e.ts:25). Fix: the type is emitted from the contract
  types section only; the cast bridges through `unknown`; the e2e
  reuses the narrowed `address`. `tsc --noEmit -p tsconfig.json` now
  reports zero errors in this packet's files; the remaining errors are
  the pre-existing DB-03/05/06 rows (`topology-client.ts`,
  `documents/vocabulary.ts`, `families/vocabulary.ts`) — not this
  packet's write scope (disclosed, not fixed).
- **MINOR (non-finite batch cursor).** `1e999` parses as `Infinity`;
  the cursor guard checked `typeof === "number" && > 0` only, poisoning
  the checkpoint so every reconnect asked `from=Infinity` in an
  unbounded fixed-delay loop. Fix: `Number.isFinite(batch.nextSequence)`
  in the guard.
- **MINOR (CRLF never parsed).** The SSE block parser split on `\n\n`
  only; spec-legal CRLF frames never dispatched (zero events, zero
  rejections, no lastError — a silent stall). Fix: the parser
  normalizes `\r\n` and lone `\r` to `\n`, holding back a trailing CR
  that may be the first half of a CRLF torn across chunks.
- **NOTE (hung connect).** The subscribe fetch had no connect timeout
  and the watchdog is off by default, so a hung TCP connect sat in
  connecting-limbo forever. Fix: `connectTimeoutMs` (default 15 000 ms)
  aborts the attempt into the normal reconnect path with a surfaced
  "connect timed out" error; the timer stops when the response opens,
  so live streams are unaffected. (The watchdog-off-by-default limit
  below stands — it is a design choice, now disclosed next to the
  connect timeout.)
- **Inspector MINOR (citation drift).** The V1 selector anchors above
  drifted by 1-10 lines (files and symbols were all correct);
  re-verified at pin fa99b925 and corrected (including two the
  inspector did not list: `ForAllIndexes` `:13`, `ForCounter`/
  `ForTimeSeries` `:15`).

### Tests (red → green)

Seven new rows in `tests/changes-subscription.test.mjs`, each driving
the real generated client against a scripted `/v1/streams` SSE server
(`startHostileStream`) that records every connection's `from` resume
parameter:

1. *checkpoint-ahead-of-gap* — batches 1,2 and 5,6 delivered, 3,4 shed,
   marker `resumeFrom=3`: reconnect asks `from=3`, 3,4 surface as the
   replayed backlog, 5,6 count as exactly 2 at-least-once duplicates.
2. *surviving trigger* — trigger(seq 4) survives its shed batch(3,4):
   3,4 surface on resume, `duplicateCount` 0.
3. *sticky rewind* — a post-marker batch(7,8) cursor (8) must not move
   the checkpoint off the boundary (asserted mid-test as 2); the
   reconnect asks `from=3` and the whole shed range 3..6 surfaces.
4. *CRLF* — spec-legal CRLF frames (including one torn across writes so
   the CR lands at a chunk boundary) parse: events surface, state goes
   live, nothing is rejected.
5. *non-finite cursor* — a hand-written `nextSequence: 1e999` frame:
   the event surfaces, the checkpoint stays finite, and the reconnect
   never sends `from=Infinity`.
6. *hung connect* — a server that accepts and never responds: the
   connect aborts within `connectTimeoutMs`, surfaces "timed out", and
   retries.
7. *per-event fail-closed validation* — a batch mixing a `1e999`
   sequence and an unknown kind with one valid event: exactly the valid
   event surfaces, both invalid ones are counted rejections.

- **Red (pre-fix client):** `node --test tests/changes-subscription.test.mjs`
  → exit 1, **8 pass / 6 fail** — every failing row is a new one, each
  for the documented reason (rows 1-3: lost/suppressed gap or checkpoint
  8 ≠ 2; row 4: silent stall; row 5: `checkpoint Infinity`; row 6:
  never surfaces; row 7 did not exist yet).
- **Green (post-fix):** exit 0, **15/15** (rows 7 added with the fix,
  red-proven via mutation below).
- **Mutation discrimination (warden's own two surviving mutations,
  re-applied to the fixed client, then restored by regeneration):**
  - drop-marker checkpoint rewind disabled → exit 1, **13 pass / 1
    fail** (row 1 fails: reconnect asked `from=7`, gap lost) — the
    warden's mutation 1 no longer survives.
  - client per-event batch validation disabled (`if (false)`) → exit 1,
    **14 pass / 1 fail** (row 7 fails) — the warden's mutation 4 no
    longer survives.
  - after each mutation the client was regenerated and the suite re-run
    green (exit 0).

### Repair receipts

All commands run in the packet worktree; exit codes recorded exactly.

| Command | Exit | Note |
| --- | --- | --- |
| `node --test tests/changes-subscription.test.mjs` (pre-fix, new rows only) | 1 | RED: 8 pass / 6 fail — all six new rows fail for the documented reasons |
| `node --test tests/changes-subscription.test.mjs` (post-fix) | 0 | 15/15 pass (8 fixture rows + 7 hostile rows) |
| `node --test tests/changes-subscription.test.mjs` (mutation: rewind disabled) | 1 | 13 pass / 1 fail — the warden's mutation 1 is now caught |
| `node --test tests/changes-subscription.test.mjs` (mutation: validation disabled) | 1 | 14 pass / 1 fail — the warden's mutation 4 is now caught |
| `node --test tests/changes-model.test.mjs` | 0 | 16/16 pass (unchanged) |
| `node scripts/generate-client.mjs --check` | 0 | client.ts in sync with the contract |
| `./node_modules/.bin/tsc --noEmit -p tsconfig.json` | 2 | all 3 packet-owned errors fixed (duplicate `StreamChangeKind`, the cast, the port destructure); the remaining errors are pre-existing DB-03/05/06 rows outside this packet's scope — zero errors in this packet's files (verified by filtering the output) |
| `vp check --fix` then `vp check` | 0 | 112 files formatted, 104 linted clean |
| `vp run unit:test` | 0 | 119 pass / 0 fail (112 + 7 new) |
| `vp run playwright:e2e` | 0 | 46 passed |
| `node scripts/real-host-probe.mjs` | 0 | `"pass": true` — live-delivery, slow-consumer (31,181 frames shed, 24,000 flood writes accepted, 128 recovered, health held), checkpoint-resume (checkpoint 24,008, 3 redelivered, exact backlog, no duplicates) |
| `cargo fmt --all -- --check` | 0 | no Rust changes; gate green |
| `cargo check --workspace --locked` | 0 | no Rust changes |
| `cargo test --workspace --locked` (run 1) | 101 | 807 passed / 1 failed under concurrent fleet-build load: `streaming::tests::publication_sink_never_blocks_a_commit_behind_a_wedged_poll` panicked "the commit blocked 1.499s behind a wedged subscriber poll" (src/streaming/tests.rs:1673) — a wall-clock liveness bound; this packet touched zero Rust files |
| `cargo test --workspace --locked publication_sink_never_blocks_a_commit_behind_a_wedged_poll` (run 2, the flake re-run) | 0 | 1 passed / 0 failed in 0.43s on the idle moment — both runs recorded honestly |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` | 0 | no Rust changes |
| `TERRABASE_V1_ROOT=… TERRASCALE_WORKSPACE_ROOT=… ./scripts/check.sh` | 0 | "TerraBaseV2 foundation checks passed" (the workspace-root variable is required from a nested worktree, as disclosed above); `cargo-deny` recorded unrun by the script's own fallback (unavailable on this host) |
