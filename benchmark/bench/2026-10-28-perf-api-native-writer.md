# Native response body writer for usage metering, 2026-10-28

## Status

DECISION: reject the native-writer metering candidate. Reason: over real HTTP, an
error thrown after a handler advanced bytes into the response writer, but before
its first flush, answers `500` with those bytes in front of the error document.
The candidate metered them, so the bill matched the bytes sent, but the error
response was corrupt. The baseline answers the error document alone. Kestrel's
own writer keeps those pre-start bytes, and `HttpResponse.Clear()` cannot discard
them. The only way to keep the clean error is to buffer privately until the first
flush. That is the baseline stream adapter's cost again, so the candidate's
purpose is gone. No timing was run and no latency, CPU or <10% overhead claim is
made.

Candidate `47053ce23b89be1ba6f0c0dcff9f2d7b993ac6d5` (production part:
[2026-10-28-perf-api-native-writer-candidate.patch](2026-10-28-perf-api-native-writer-candidate.patch),
unapplied) against baseline `88df7e052aed185f4fbff530696cc1df98170f24`; `src/` and
`tests/` are identical between that baseline and `origin/main`
`8ce212295`. It replaced the `Response.Body` counting stream in
`UsageRequestMetering` with an `IHttpResponseBodyFeature` that counts the
server's native `PipeWriter`.

## Controls

`tests/TerraBase.Fast/Management/UsageWriterTests.cs` (branch
`fix/usage-writer-metering-controls`) drives the real `Program` over its client
listener. A test-only endpoint is registered through
`AddTerraScaleEndpoints([EndpointRegistration.Create<…>()])`, the existing seam
also used by `ShapeRetentionTests`. It attributes each request to a database of its own
with `UsageRequestMetering.Attribute`. Per-minute `client_bytes|<region>||sent`
comes from `UsageMeter.GetObservationsAsync`, at minute granularity, under a
`FakeTimeProvider`. Each test owns its server, so all run in parallel. W = 1000
bytes advanced into `Response.BodyWriter` without a flush.

| Control | What the handler does | Passes when |
| --- | --- | --- |
| throw | Advance W, throw | Body is exactly `{"error":{"code":"internal_error"}}`, status 500, minute 0 meters its 35 bytes |
| abort | Advance W, wait for `RequestAborted`, return normally after the client cancels | Nothing is metered |
| minutes | Flush 2000 in minute 0, flush 2000 in minute 1, return with W unflushed | Each minute meters what the client read in it; `ReportActive` while open shows minute 0 |
| stream | Advance W, write and flush 2000 through `Response.Body`, stay open across the minute | Minute 0 meters every byte the client read up to the stream's last byte, also while open |
| start | Advance W, `Response.StartAsync()`, cross the minute | Minute 0 meters what `StartAsync` sent by Kestrel's own unflushed count before and after; the rest is metered when sent |

## Results

Delta, `ts-heavy-any nice -n 19`, Release, .NET SDK `11.0.100-rc.1.26425.128`,
pinned ts-dotnet `36d114877a69d18fba33b27ad362bd3e68e9e7ff` (clean), Turso
assets from `scripts/vendor/download-turso-native.sh`. Raw per-test outcomes, probe
facts and failure messages:
[2026-10-28-perf-api-native-writer-controls.jsonl](2026-10-28-perf-api-native-writer-controls.jsonl).

| Control | Baseline (runs 2, 3) | Candidate (runs 2, 4) |
| --- | --- | --- |
| throw | pass: `500 {"error":…}`, metered [35, 0] | **fail**: `500 <1000 × "w">{"error":…}`, metered [1035, 0]; `HasStarted` false after the advance |
| abort | pass: metered [0, 0] | **fail**: metered [1000, 0]; the client cancelled before any header arrived |
| minutes | pass: read [2000, 2000], metered [2000, 2000] | pass: read [2000, 3000], metered [2000, 3000] |
| stream | pass: read [2000, 0], metered [2000, 0] | **fail**: read [3000, 0], metered [2000, 1000]; open minute 0 shows 2000 |
| start | pass: start sent 0, read 0, metered [0, 0] | pass (run 4): start sent 0, read 1000, metered [0, 1000] |

| Suite filter (run 2) | Baseline | Candidate |
| --- | --- | --- |
| `UsageWriterTests` | 5/5 | 1/5 (start's first oracle, below) |
| `Usage*` (includes the five above and the 206-line `UsageBodyBytesTests`) | 78/78 | 74/78, the same four |
| `DatabaseChanges*` | 48/48 | 48/48 |

## Review findings

1. Response integrity, error before the first flush: **confirmed**. The candidate
   prepends the 1000 pending bytes to the error JSON. A narrow fix would need
   private buffering until the first flush, so the candidate is rejected (see
   Status).
2. Pending bytes counted after an abort: **confirmed**. The candidate meters 1000
   bytes the client never received. Guarding `Committed()` with
   `RequestAborted` would fix this control, but the candidate is already rejected
   by finding 1. When an exception escapes, the candidate counts no tail, and in the
   throw control the pending bytes were sent and metered inside the 500 body.
3. Pending bytes flushed by a later stream write: **confirmed**. A
   `Response.Body` write and flush on Kestrel sends the writer's pending bytes too
   (3000 read in minute 0). The candidate metered 1000 of them in minute 1 and hid
   them from `ReportActive`. `StartAsync` sends no body bytes: Kestrel's unflushed
   count went from 1000 to 1211 (the headers were added, nothing flushed), and both
   arms meter correctly there.
4. Writer path across a minute boundary: the minutes control covers it. Both arms
   pass, per minute and while open.

Baseline fact for follow-up (not changed here): the baseline never sends bytes a
handler leaves unflushed in `Response.BodyWriter`: the minutes tail, the start
bytes and the stream-path pending bytes. It does not meter them either, so the
bill matches what was sent. Production JSON endpoints were not audited for this
path.

## Attempts

1. Build failed in the new test file (`CS0246 Unavailable`, `IDE0305`). Fixed in the test.
2. Both arms completed. The start control's first oracle, Kestrel's unflushed count
   after `StartAsync` between 0 and W, failed on the candidate with 1211 because the
   count includes header bytes. The oracle was corrected to compare the count
   before and after the start.
3. The baseline arm passed 5/5 with the corrected oracle. The candidate arm was
   killed mid-build by a T3 server restart (infrastructure, no result).
4. Candidate arm only: 2/5, as tabled.
