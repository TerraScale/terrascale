# DB-04 — Live updates: SSE views driven by the event backbone via the API host

Ticket: TerraScale/TerraBaseV2#21 (DB-04, milestone M3 Live). Worktree
`.worktrees/terrabasev2-db04`, branch `fleet/db04-2`, base `da0df43`.
Status: implemented; receipts below.

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

- `IDocumentChanges.cs:6` `ForDocument`, `:13` `ForAllDocuments`,
  `:20` `ForDocumentsStartingWith`, `:27` `ForDocumentsInCollection` —
  the feed's Documents selector group + collection filter.
- `IIndexChanges.cs:6` `ForIndex`, `:12` `ForAllIndexes` — the Indexes
  group.
- `ICounterChanges.cs:6` `ForAllCounters`, `:11` `ForCounter` — the
  Counters group.
- `ITimeSeriesChanges.cs:6` `ForAllTimeSeries`, `:11` `ForTimeSeries` —
  the Time series group.
- `IOperationChanges.cs:8` `ForOperationId`, `:15` `ForAllOperations` —
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
  the opt-in `staleMs`) drive reconnects.
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
