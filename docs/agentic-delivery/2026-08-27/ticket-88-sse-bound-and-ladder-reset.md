# Ticket TerraBaseV2#88 — TS SDK follow-ups: SSE bound placement + slow-cadence ladder reset

Packet `fups-ts` on branch `fleet/fups-ts`, base `6da6565` (`origin/develop`
HEAD at packet start). Both warden re-gate MINORs were repaired red-then-green
at the public session seam; each repair is one commit with its pinning test in
the same commit.

## What landed

1. **SSE line bound placement** (`src/sync/v2/session.ts`,
   `sseOverFetchWith`): the 4096-unit line bound was evaluated on the read
   buffer *before* complete lines were drained, so it measured the reader
   chunk, not a line — one chunk carrying many complete newline-terminated
   events (each individually far under the bound) false-positived as
   `stream_limit` and knocked a fully well-behaved session off `live`. The
   bound is now evaluated only after every complete line is drained, so it
   measures the residual — the one line the host has not terminated yet.
   Red test: `a well-behaved multi-event chunk does not trip the line bound`
   (80 trigger events, chunk ~5.9k units, longest line ~120 units).
2. **Reconnect ladder reset** (`src/sync/v2/session.ts`,
   `#scheduleStreamReconnect`): the ladder reset whenever the dying stream
   had been open for the 1 s stability window, so a stream dying on any
   cadence just past the window — with a healthy delta endpoint — reset its
   own ladder every cycle and reconnected unbounded (never reaching
   `MaxReconnectAttempts`). The ladder now forgets previous attempts only
   after a **sustained-stability horizon**: no stream failure for longer
   than every gap the ladder itself can schedule (the 30 s delay cap plus
   one 1 s window = 31 s). Failures inside the horizon accumulate. Red test:
   `a stream dying on a slower-than-window cadence still exhausts the
   ladder` (stream killed every 1.1 s, delta endpoint healthy,
   `MaxReconnectAttempts: 3`).

No public option, default, route, DTO, or protocol surface changed
(`sdk-route-manifest.json` re-generated identical: 161 routes).

## Decision records

### DR-1 — drain complete lines before evaluating the SSE line bound

- Chosen: evaluate the bound on the buffer residual after the drain loop.
  The residual is exactly the unterminated line, so the bound detects a
  pathological undelimited line at the same 4096-unit threshold as before
  (the existing pathological-line test stays green unchanged).
- Alternative (a): keep checking the chunk but raise the bound or add slack
  for coalesced events — rejected: any slack factor re-couples the bound to
  transport coalescing (a big-enough chunk still trips; a small-enough
  threshold still false-positives). The defect is the measured quantity,
  not the number.
- Alternative (b): check each complete line's length at slice time and drop
  the buffer check — rejected: a line that never terminates never reaches
  the slice, so the unbounded-buffer protection the bound exists for would
  be gone.
- Evidence: red run of the multi-event test failed at
  `assert.equal(session.getStatus(), "live")` with actual `reconnecting`
  (the false-positive `stream_limit`); green run passes with the
  pathological undelimited-line test unchanged.

### DR-2 (PROPOSED) — sustained-stability horizon = delay cap + one window (31 s)

- Chosen: reset the ladder iff no earlier ladder-climbing failure occurred
  within `reconnectStabilityHorizonMilliseconds` (31 s). The value is
  pinned by a structural constraint, not taste: the horizon must strictly
  exceed the largest gap the ladder itself can schedule between failures
  (a single delay is at most the 30 s cap), otherwise a late attempt's own
  backoff gap (e.g. attempt 7 at default base waits ~12.8 s; larger bases
  wait up to 30 s) would masquerade as stability and reset the ladder
  mid-outage, making exhaustion jitter-dependent. The extra window keeps
  the margin strict and preserves the 1 s window as the documented
  stability quantum.
- Alternative (a): the shipped 1 s open-duration reset — the confirmed
  defect (warden probe: 6+ stream GETs climbing, no exhaustion).
- Alternative (b): require N consecutive stability windows of open
  duration (e.g. 2× or 3×) — rejected: the same defect class one threshold
  up; any cadence just past N×window resets again. Only a horizon that
  exceeds the ladder's own scheduling range makes exhaustion
  cadence-independent inside it.
- Alternative (c): count failures per trailing window and reset only on an
  isolated failure — equivalent to the chosen gap form; the single
  timestamp is simpler and needs no ring buffer.
- Alternative (d): never reset — rejected: a genuinely recovered session's
  next distinct outage would inherit a nearly-exhausted ladder and fail
  terminally without ever misbehaving; fresh-outage semantics after real
  stability are part of the documented ladder contract.
- **PROPOSED for parent ratification**: this changes documented reconnect
  behavior. The class docstring previously promised "a stream that stays
  open for a stability window starts a fresh ladder on its next failure";
  the fresh ladder now requires 31 s with no stream failure. The ladder is
  correspondingly sticky across outages separated by less than that (a
  session whose stream dies every ~10 s now terminates after
  `MaxReconnectAttempts` total failures instead of reconnecting forever —
  intended, but a behavior change consumers can observe). No option or
  default changed; ratify the horizon semantics (and the 31 s constant) or
  direct a different trade-off.
- Side effect worth naming: the mirror regime (healthy long-lived stream,
  dying delta endpoint) was *also* unbounded at base, because each
  reconnect tick reopened the >1 s-old stream and reset the ladder; the
  horizon bounds that regime too, for the same reason.

### DR-3 — the fresh-ladder half of DR-2 is not separately test-pinned

A deterministic green test for "a stream stable past the horizon starts a
fresh ladder" costs >31 s of wall clock (real timers only — the workspace
no-mocks rule rules out timer mocks, and a test-injectable horizon would
widen the public options surface for test convenience). The reset branch is
exercised by construction (reset iff gap ≥ horizon) and is the same branch
the slow-cadence red test drives from the other side. Disclosed as a limit,
not claimed as covered.

## Receipts (exact commands and exit codes)

Base verification (before any edit; worktree at `6da6565`):

- `npm ci --no-audit --no-fund` — exit 0
- `npm run build` — exit 0
- `npm run test:unit` — exit 0 (132/132)
- `node --test dist/release-package.test.js` — exit 0 (3/3). **Discrepancy
  against the packet brief**: the brief expected this to fail at base as a
  known CI-owner issue; it passed at base in this environment. Recorded as
  found; the file was neither edited nor investigated further (not this
  packet's scope).
- `npx playwright test --project=safari-current` — exit 1, host-blocked:
  `libgstreamer-1.0.so.0: cannot open shared object file` (webkit
  MiniBrowser fails to launch). Matches the brief's known pre-existing
  failure; untouched.

Red-then-green, fix 1 (SSE bound):

- `npm run build` — exit 0
- `node --test --test-name-pattern "multi-event chunk" dist/sync-v2-session.test.js`
  — exit 1 (red for the right reason: `AssertionError: a fully
  well-behaved host keeps the session live`, actual `reconnecting` — the
  false-positive `stream_limit`)
- after the fix, `node --test dist/sync-v2-session.test.js` — exit 0
  (15/15, including the unchanged pathological-line test)

Red-then-green, fix 2 (ladder reset):

- `npm run build` — exit 0
- `node --test --test-name-pattern "slower-than-window" dist/sync-v2-session.test.js`
  — exit 1 (red for the right reason: `condition not reached within the
  timeout` after 30.3 s — status never reached `failed`; the ladder reset
  every 1.1 s cycle)
- after the fix, `node --test dist/sync-v2-session.test.js` — exit 0
  (16/16; the slow-cadence test exhausts `MaxReconnectAttempts: 3` and
  records `reconnect_exhausted` in ~4.4 s)

Post-change validation:

- `npm run typecheck` — exit 0
- `npm run build` — exit 0
- `npm run check:route-manifest` — exit 0 ("161 routes" — unchanged)
- `node --test dist/sync-v2-session.test.js dist/sync-v2-conformance.test.js dist/sync-v2-frame.test.js dist/sync-client.test.js dist/sync.test.js`
  — exit 0 (56/56; was 54/54 at base, +2 new tests)
- `npm run test:unit` — exit 0 (134/134; was 132/132 at base)
- `npm run test:integration` — exit 0; the DevHost/E2E-gated tests skip in
  this environment (no `TERRASCALE_API_BASE_URL` /
  `TERRASCALE_AUTH_API_BASE_URL` / E2E credentials), so no live evidence
  was gathered and none is claimed
- `npx playwright test --project=chrome-current --project=firefox-current --project=edge-current`
  — exit 0 (3 passed)
- `npx playwright test --project=safari-current` — exit 1, identical
  `libgstreamer` host block as at base (not this packet's failure)

Timing note: the two ladder tests are wall-clock sensitive (1.1 s kill
cadence); a single failure under concurrent fleet load should be re-run
once and both runs recorded. Neither failed during this packet's runs.

## V1 citations

None. Per the ticket, these are SDK-side defect repairs of SE-09 code; the
defect evidence lives in the SE-09 gate records, and no V1 behavior is
mirrored or claimed (there is no V1 TS SDK surface to cite at pin
`fa99b925`).

## Deferred

- A dedicated >31 s stability lane pinning the fresh-ladder half of DR-2
  (see DR-3) — owner: parent, if the ratification in DR-2 wants it pinned.
- Cross-SDK semantic parity of these two repairs (the sibling .NET SDK
  packet #89/#93-SDK may carry the same defect pair in its sync client) —
  owner: parent.
- `release-package.test.js` CI-owner discrepancy (passes here at base,
  brief expected failure) — owner: CI owner, as the brief assigned.

## Proposed collision actions

None. All edits stayed inside the packet's write scope:
`src/sync/v2/session.ts`, `src/sync-v2-session.test.ts`, and this record.
No changes to `package.json`, lockfiles, CI files, contract fixtures, or
generated artifacts (`sdk-route-manifest.json` regenerated identical).

## Disclosed limits

- Any fixed horizon is gameable by a slower cadence: a stream whose deaths
  are separated by more than 31 s of stability still resets the ladder.
  The repair makes exhaustion cadence-independent *inside* the horizon
  (which covers the confirmed >1 s pathology with a wide margin); it cannot
  make "sustained stability" duration-free.
- The repair bounds the client's reconnection; it does not diagnose or fix
  why a host's stream dies (idle timeouts, LB cuts, and similar remain
  host-side concerns surfaced through the terminal `failed` state and the
  documented fresh-`connect()` recovery).
- The SSE line bound now trips on an undelimited line once its residual
  exceeds 4096 units — the same detection threshold as before; detection
  latency for a pathological line is unchanged in practice (it previously
  also tripped on the first over-bound chunk).
- The backoff shape (base × 2^attempt, jitter 0.5–1.0×, 30 s cap) is
  untouched by this packet.

## Explicit non-claims

- No live DevHost/E2E evidence (environment-gated tests skipped; none
  simulated).
- The safari browser lane's failure is not resolved, not investigated, and
  not this packet's (host-blocked at base and after).
- No claim about the `release-package.test.js` CI-owner issue beyond the
  recorded base observation (it passed here).
- No V1 parity claims of any kind.
- No performance claims about either repair.
