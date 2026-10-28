# API request overhead: first shipped speedup, 2026-10-28

Shipped in [#854](https://github.com/TerraScale/terrascale/pull/854), merged as `80c268f64` at 18:41 UTC. The change counts
client bytes on the server's own response pipe and records request logs in reusable per-database rings. Result:

- **Quiet same-host before/after:** a small speedup measured in all four keyed api read cells.
  - Process CPU per request fell 0.9–3.1%.
  - Allocation fell 1.6–2.4%.
  - Server overhead p50 fell 0.9–2.1%.
  - Throughput rose 0.2–1.8%.
- **Live in gru, sjc and fra:** the change is deployed but does not resolve. Every serving TerraBase already uses its full
  1-CPU limit while idle (background work), so live latency and throughput are dominated by that. No live speedup is claimed.
- **Target:** the original <10% server-over-engine target remains unmet. Server overhead is still ~85–115 µs over a 4–7 µs
  engine read.

## Decision

The earlier evidence (index, CPU and allocation rankings) attributed costs only to the nearest sampled frame, never inclusively.
A throwaway ablation therefore ranked each middleware by removing it:

- Setup: VsTurso keyed api read lane; beta, 12 vCPU; `ts-heavy-any nice -n 19`; 10 s warmup and 5 s cells; 2 repeats.
- Deltas are overhead p50 (server minus engine) for small/c1, small/c16, medium/c1 and medium/c16. Baseline overhead p50 was
  86–87, 108–109, 92–93 and 116 µs.

| Removed | Overhead p50 Δ µs | CPU µs/op Δ | B/op Δ |
| --- | --- | --- | --- |
| Usage metering (`UseTerraBaseUsage`) | −7, −8, −7, −7 | −14…−19 | −2,700 (small c1) |
| QoS admission (`UseTerraBaseQos`) | −7, −9, −8, −8 | −8…−12 | −1,100 |
| Project check / key watch | −3…−6 | small | small |
| Key watch alone | −1.5 (c1), −3 (c16) | ~0 | small |
| QoS memory, CORS, pressure, private responses, placement, catalog gate | within ±2 | within noise | within noise |
| Document cache quota reconcile (inside QoS) | 0 | 0 | 0 |

DECISION: usage metering. Its overhead p50 saving tied with QoS, but it saved the most CPU and allocation, and removing it also
removed the c1 tail stalls (engine p99 ~150 µs down to ~11 µs). Inside usage metering, a second ablation showed that the meter's
`RecordRequest` cost ~0. Request-log recording carried the cost: removing `logs.Record` alone was worth −15 µs CPU/op at c1.

## Change (#854)

- **Byte counting:** usage metering wraps the server's `IHttpResponseBodyFeature` and counts bytes where they enter Kestrel's pipe,
  its stream or a sent file. It no longer replaces `Response.Body` with a counting stream, which put a copying `StreamPipeWriter`
  in front of every JSON result. Requests that cannot have a body keep their request stream.
- **Request logs:** each database's unsaved requests are plain values in a ring that a full database overwrites in place. The
  evicted entry's 4 KiB lease moves to the newest request. Before, every request allocated a log record, a ULID string, an entry
  and a lease, all living until 100 newer requests evicted them, and took two global memory-budget locks.
- **Unchanged:** bytes, minute attribution, counts, log fields, the 100-entry/24 h retention, the pending bound, per-entry process
  admission, drops and flush/read output.
- **New Fast test:** `ClientBytesTests` asserts exact `client_bytes` over real HTTP: a PUT's request and JSON response, a
  body-less GET receiving 0 bytes, and a 404 error body written to the stream.

## Quiet before/after (beta)

Setup:

- **Harness:** `benchmark/dotnet/VsTurso/run-server-overhead.sh` through
  `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19`, with the other heavy slots reserved.
- **Arms:** before = main `06eab3285`; after = the #854 source (identical changed files). 3 alternating fresh-process pairs:
  before/after, after/before, before/after.
- **Verified identity:** only the server DLL differed. Before `terrabase.dll`
  `99924164ccaac2f5c90680b09d6511bd39ba1f3d7a6ff88d54775e39d28cd380`, after
  `b8e3aa1d79122e1244b01bb4a5e48089c0b57c9641f1b8e2b90e5ecc024241f1`. ts-dotnet `35c930efa856edf24223eed3e33c512c0d16ec13`.
- **Runtime:** .NET 11 rc.1, workstation concurrent GC, TieredPGO.
- **Host:** load average 3.6–10.1 at arm start (it includes the benchmark itself); CPU pressure some-avg10 6.8–17.8%. The first
  before arm started right after the Fast build, which accounts for the highest values.

Median of 3 (all values per arm in the run log):

| Cell | ops/s | Client p50 µs | CPU µs/op | B/op | Server p50 µs | Overhead p50 µs |
| --- | --- | --- | --- | --- | --- | --- |
| small c1 | 6,091 → 6,154 (+1.0%) | 154.5 → 152.1 | 220.7 → 218.7 (−0.9%) | 30,295 → 29,581 (−2.4%) | 90.2 → 88.6 | 86.2 → 84.5 (−2.0%) |
| small c16 | 36,139 → 36,794 (+1.8%) | 312.6 → 305.9 | 204.6 → 199.1 (−2.7%) | 29,895 → 29,221 (−2.3%) | 113.8 → 113.1 | 108.3 → 107.3 (−0.9%) |
| medium c1 | 5,745 → 5,759 (+0.2%) | 162.6 → 162.3 | 228.2 → 221.1 (−3.1%) | 46,245 → 45,405 (−1.8%) | 96.5 → 95.6 | 91.5 → 90.6 (−1.0%) |
| medium c16 | 29,720 → 30,238 (+1.7%) | 361.2 → 353.2 | 224.7 → 220.4 (−1.9%) | 45,454 → 44,706 (−1.6%) | 122.2 → 120.0 | 115.6 → 113.2 (−2.1%) |

- B/op is lower in every arm and CPU/op in every cell.
- p99 ranges overlap (for example small c1 client p99 335–348 → 330–351 µs), so no tail claim is made.
- The first commit alone measured −430…−600 B/op and ~−1 µs overhead p50.

Fast on beta, same build: `ClientBytesTests` 1/1, `RequestLogApiTests` 8/8, `RequestLogMemoryTests` 4/4,
`RequestLogFilterContractTests` 1/1, `UsageApiTests` 46/46, `SqlMeteringTests` 12/12 and `DatabaseChangesTests` 28/28.

PR CI:
- First required Fast run: 3168/3169; the one failure was the open flake #787, a disk-reservation assertion this change does
  not touch.
- Rerun: required check passed, then merge-queue qualification passed.
- Slow (non-blocking) failed twice on unrelated tests: an auth token-request timeout, and the catalog-anchor/writer-drain
  restart tests (#733 family).

## Live before/after (gru, sjc, fra)

`benchmark/scripts/api-usage-live.sh` runs on each serving node through `ts-orch netbird ssh` (script on stdin):

- It creates one synthetic organization (Enterprise plan, so QoS does not rate-limit it), project, database and admin API key
  through the internal listener, and seeds a 256 B and a 4 KiB document.
- It drives keyed `GET .../documents/by-key` on `127.0.0.1:8080` at a fixed 1,000 req/s target (4 paced keep-alive processes),
  10 s per cell, 2 sizes × 3 repeats.
- It records client latency, achieved rate and the TerraBase process CPU (`/proc/<pid>/stat`), with a 5 s idle window before
  each cell for subtraction.
- The tenant is always deleted afterwards. Load is bounded to 60 s per region per run.

| Region | Arm | Revision | Started (UTC) | Size | Cells | Errors | ops/s | p50 ms | p99 ms | Idle TerraBase CPU (cores) | CPU µs/op per cell (idle-subtracted) |
| --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| gru | before | `30022135f` | 16:16:02 | small | 3 | 0 | 488 | 1.92 | 76.3 | 1.00 | −4.9, −9.5, −5.8 |
| gru | before | `30022135f` | 16:16:02 | medium | 3 | 0 | 443 | 2.03 | 77.7 | 1.00 | −5.1, −5.0, −0.7 |
| gru | after | `80c268f64` | 19:36:17 | small | 3 | 0 | 764 | 1.49 | 69.2 | 1.00 | 0.8, −5.7, −8.6 |
| gru | after | `80c268f64` | 19:36:17 | medium | 3 | 0 | 626 | 1.67 | 69.9 | 1.00 | −6.2, −10.3, −3.7 |
| sjc | before | `30022135f` | 15:57:58 | small | 3 | 0 | 895 | 2.12 | 38.3 | 1.00 | 27.6, −4.2, −0.3 |
| sjc | before | `30022135f` | 15:57:58 | medium | 3 | 0 | 955 | 2.26 | 40.4 | 0.96 | 127.8, 36.1, 2.7 |
| sjc | after | `c80d34fe6` | 19:46:50 | small | 3 | 0 | 453 | 2.60 | 50.9 | 1.00 | −0.5, −4.0, −4.2 |
| sjc | after | `c80d34fe6` | 19:46:50 | medium | 3 | 0 | 426 | 3.16 | 101.0 | 1.00 | −2.1, −2.8, 3.6 |
| fra | before | `1035dd682` | 16:18:52 | small | 3 | 0 | 508 | 3.02 | 46.5 | 1.00 | 30.3, 10.8, −1.4 |
| fra | before | `1035dd682` | 16:18:52 | medium | 3 | 0 | 629 | 2.68 | 48.0 | 1.00 | −29.7, 566.2, −2.2 |
| fra | after | `c80d34fe6` | 19:54:51 | small | 3 | 0 | 715 | 2.60 | 42.9 | 1.00 | 2.7, −7.6, −4.3 |
| fra | after | `c80d34fe6` | 19:54:51 | medium | 3 | 0 | 579 | 2.73 | 54.9 | 1.00 | −2.4, 3.0, −4.6 |

Reading:

- **Saturated nodes:** every serving TerraBase used ~1.0 core, its full cgroup limit (`cpu.max 100000 100000`), while idle and
  serving no client load.
  - Idle-subtracted CPU per request is therefore noise around zero, sometimes negative.
  - The 1,000 req/s target was never reached (143–998 req/s per cell).
  - Latency (per-cell p50 1.4–3.7 ms; median p99 38–101 ms, single cells up to 365 ms) is CPU queueing behind background work. Beta shows ~0.15 ms p50 for the same request.
- **Different "after" revisions:** gru after ran `80c268f64` (#854 itself). sjc and fra after ran `c80d34fe6`, which also contains #855
  and #857. Its earlier after attempt on `c1337d102` ended when the `c80d34fe6` rollout restarted the node mid-run (19:41).
- **Not attributable:** none of these live differences can be attributed to #854.
- **fra after, third attempt:** after its rollout restart (19:43), fra answered the synthetic organization create with 503 twice
  (19:50, ~19:53), with repeated "Waited s for a database's writer" warnings in its log. No tenant was created: every acting
  user in that window lists no organizations. The third attempt, after a production hold on unrelated work was lifted, completed.

## Cleanup

- Every run that created a tenant deleted it, and the organization then read 404. Key revocation returned 503 for up to four
  15 s attempts on busy nodes before 204.
- The second sjc attempt (15:51:49) is the exception: it lost the acting user id when its work directory was removed, so its
  teardown stopped after revoking the key.
  - The user was recovered from TerraBase's own operator audit log, then the database, project and organization were deleted
    (organization 404 at ~15:57).
  - The script now emits the synthetic user id (not a credential) in its tenant row and retries every teardown step independently.

## Remaining measured costs (next candidates)

- **Request-log flush:** after #854, removing the 10 s request-log flush to the catalog was worth −9 µs CPU/op at c1, and removing
  ULID generation −11 µs. A constant id collapses the flush to one document, so that variant also measures the flush. Both remove
  the c1 engine p99 stalls (~160 µs → ~11 µs). The cost scales with ticks rather than requests: c16 amortizes it to ~1–2 µs.
- **QoS admission:** −7…−9 µs overhead p50 when removed; not investigated further.
- **Production background saturation:** this dominates every live API number. In an 18-minute gru log window, the top warnings
  were 308 "A database's writer was held … by …" messages, plus replication stream reconnects and backup-erasure retries.
  This belongs to the engine and replication lanes.
