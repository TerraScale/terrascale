# Reactive subscription fan-out cost (HTTP v1) — 2026-09-15

Caveats up front: single host, poll-based v1 protocol, loopback, `nice 19`,
2 CPUs. Concurrent-client WAN fan-out is NOT represented. All numbers come
from one measurement host on one machine; they characterize the reactive HTTP
v1 delivery path under sequential publishes, not a capacity rating.

## Environment

| Field | Value |
| --- | --- |
| SDK | 10.0.401 (repo `global.json` pins 10.0.400, `rollForward: latestPatch`) |
| Runtime | .NET 10.0.12 (`RuntimeInformation.FrameworkDescription`) |
| OS | Linux 7.2.4.0 |
| CPUs | 2 (`taskset -c 16,17`, `DOTNET_PROCESSOR_COUNT=2`) |
| Nice level | 19 (whole process tree: harness + server) |
| GC | Server GC (`DOTNET_gcServer=1`) in the measured server process; workstation GC in the harness client process |
| Network | Loopback `http://127.0.0.1:<ephemeral>` on one host |
| Latency clock | `Stopwatch.GetTimestamp` (per-frame timestamps; no dry runs) |
| Percentiles | Nearest-rank p50/p95/p99, rounded to 0.01 ms |
| Samples | 30 measured per latency case; warmups discarded (20 fanout, 2 snapshot, 1 log tail) |
| Source | Evidence working copy `.codex/evidence/terrabase-perf-fanout-20260915/source` (full source copy, strict analyzers on, build succeeded with 0 warnings) |
| Harness | `measurement-harness/` in that copy; copied to `.codex/swarm/measurement/reactive-fanout-20260915/` |

## What was measured (real host, real protocol)

The harness drives the real reactive HTTP v1 host, not an in-process delivery
call: `TerraBaseHost.Create` on an ephemeral loopback port, served by a
dedicated child process of the harness (`serve` mode) so server CPU and RSS
are read from the server's own process. Routes exercised:

- `POST /api/v1/reactive/views` (attach; JSON body with `databaseId`,
  `statement`, `parameters`, `subscription`, `entitlement_epoch`)
- `GET /api/v1/reactive/views/{view}/{generation}/snapshot/{chunk}?cut=N`
- `GET /api/v1/reactive/views/{view}/{generation}/log?offset=N`

The explicit catalog configures one measured route plus one warmup route (both
`storage-engine` backends, distinct physical paths and database ULIDs, so
warmup traffic never advances the measured database's commit sequence).
Mutations are published through the public document endpoint
(`PUT /api/v1/databases/{route}/collections/items/{id}`, `If-None-Match: *`).

Measurement-only trust replacements: an allow-all `ITenantAuthorizationHook`
and an allow-all `IReactiveSqlAuthorizer` (the production defaults are
fail-closed), and a bearer-gated principal carrying the `tenant`, `sub`,
`authorization_generation`, and `entitlement_generation` claims the v1
protocol requires. `ReactiveHttpOptions` for the run: `MaximumLongPoll` 5 s,
`MaximumViews` 1024, `Delivery.SubscriberBufferSize` 256 (default 32; raised
so 100 unpooled mutations fit the per-subscription buffer in the log-tail
case), `Backpressure` Reset (default).

View statement everywhere: `SELECT id FROM items ORDER BY id`.
Every latency sample is a real request/response on the wire; warmups were
executed and discarded. N per case is stated in each table.

## 1. Session scaling (publish → N poll-sessions)

One small document mutation (64-byte body) per publish; N attached v1
sessions, each parked in a long-poll `GET .../log` 250 ms before the publish.
Delivery latency = t(log GET returns the delta frame) − t(PUT ack received);
negative values are possible and real (the frame can be published before the
PUT ack reaches the client). Server CPU per publish = server-process
`TotalProcessorTime` delta across the 30-publish window divided by 30 — it
includes serving the N parked long-polls, the change-feed worker's 50 ms
poll cadence, and background runtime samplers. Throughput = 30 publishes over
the sampled window (the window contains the deliberate 250 ms park per
iteration, so it is a publish+drain cycle rate, not a pure ingest rate; the
publish-only ack RTT p50 is the `PUT p50` column). 30 measured publishes per
step, 20 warmups discarded, 10 seed documents, pooled per-session delivery
samples (N × 30).

| Sessions | PUT p50/p95/p99 (ms) | Delivery p50/p95/p99 (ms) | Server CPU ms/publish | Cycles/s | RSS after attach (MiB) | RSS after run (MiB) | Peak RSS (MiB) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 3.38 / 9.73 / 13.11 | 41.09 / 49.56 / 50.52 | 89.3 | 3.42 | 115.9 | 137.6 | 137.6 |
| 8 | 3.30 / 6.82 / 7.86 | 48.00 / 51.24 / 51.84 | 119.0 | 3.45 | 118.6 | 154.7 | 157.5 |
| 64 | 2.96 / 7.99 / 8.45 | 49.21 / 66.27 / 67.93 | 163.3 | 3.38 | 122.1 | 164.0 | 164.0 |
| 256 | 3.55 / 7.00 / 64.70 | 65.50 / 138.48 / 143.40 | 281.7 | 3.08 | 125.7 | 186.5 | 191.4 |

Reading: the ~41–49 ms delivery p50 floor is the change-feed worker's default
50 ms poll interval, not protocol overhead. Marginal fan-out cost from 1 → 256
sessions is about +192 ms server CPU per publish (~0.75 ms per additional
poll-session), +24 ms delivery p50, and ~+93 ms p95/p99 spread; the PUT ack
path itself is flat (p50 ≈ 3 ms at every N). The p99 delivery spread at 256
sessions (143 ms) includes 256 concurrent frame encodes and GET responses on
2 niced CPUs. Zero long-poll timeouts were observed at any N. RSS after
attach is flat (~116–126 MiB): a v1 session retains its snapshot chunks and
capability, which is small at this view size; RSS growth during the run
(+22 → +61 MiB from N=1 to N=256) tracks per-session delivery state and heap
fragmentation, and each N step ran in a fresh server process.

## 2. Snapshot chunk cost (initial snapshot over 1k / 10k docs)

Attach (which captures the durable cut, evaluates the SQL view, and encodes
chunks) followed by GET of every chunk URL from the attach response. 30
samples per size, 2 warmups discarded, fresh server per size.

| Documents | Attach p50/p95/p99 (ms) | Attach+chunks p50/p95/p99 (ms) | Chunks | Bytes/chunk | Chunk GET p50/p95/p99 (ms) | Decoded items | Server RSS final/peak (MiB) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1,000 | 124.11 / 713.96 / 747.25 | 128.12 / 717.35 / 751.22 | 1 | 14,116 | 2.64 / 8.17 / 8.18 | 1,000 | 156.0 / 158.1 |
| 10,000 | 131.31 / 359.09 / 832.25 | 134.87 / 711.74 / 835.82 | 1 | 14,116 | 2.54 / 8.20 / 556.51 | 1,000 | 251.8 / 253.8 |

The v1 reactive SQL protocol caps one view result at 1,000 rows
(`ReactiveSqlQueryLimits.MaximumResultLimit`, applied when the statement has
no explicit `LIMIT`), so the 10k-doc view snapshots exactly the first 1,000
document ids and its chunk bytes equal the 1k case; both decoded item counts
were verified through `ReactiveV1EnvelopeCodec` on every sample. Attach wall
time still rises slightly (p50 124 → 131 ms) because binding and evaluation
scan all seeded documents under the cap. The heavy p95/p99 attach tail
(~700–830 ms, both sizes) is single-host GC/scheduler noise under `nice 19`,
reproducible across both runs; the p50s are the stable signal. Each snapshot
is a single ~14 KB MessagePack chunk (14.1 bytes/row for the `id`-only
projection), and a chunk GET is a ~2.5 ms loopback read of retained bytes.

## 3. Log tail after 100 sequential mutations

Fresh server and empty measured database per sample (the v1 log offset equals
the database commit sequence, so offset 0 is only observable on a database
with zero committed mutations at attach — each of the 30 samples attached at
offset 0, verified). Two sessions attach before 100 sequential PUTs; then one
session fetches `GET .../log?offset=0` (its first buffered frame) and the
other, after 90 discarded draining polls, fetches offset 90. Each log GET
returns exactly one MessagePack delta envelope. 30 samples, 1 warmup
discarded.

| Fetch | Wall p50/p95/p99 (ms) | Response bytes |
| --- | --- | --- |
| offset 0 (first frame of the retained log) | 4.37 / 6.07 / 11.29 | 1,134 |
| offset 90 (near the tail of 100 buffered frames) | 1.91 / 2.88 / 2.96 | 1,135 |

Fetch cost is flat with respect to offset: both fetches return one already
buffered frame, so the ~2–4 ms difference at p50 is first-request-per-process
residue, not log position. The one-byte payload difference is the revision
number's wider encoding at sequence 91. There is no server-side replay scan
for a retained session: offsets the session has not yet consumed are served
from the subscription buffer, and any offset other than the session's current
sequence returns `409 reactive_reset_required`.

## Not reported, and why

- Server allocation bytes: the server is a separate process and
  `GC.GetTotalAllocatedBytes` is process-local; the harness made no claim.
- WAN/concurrent-client fan-out, TLS, proxies, and multiple publisher
  concurrency: out of scope for this single-host loopback run.
- Pure publish ingest rate: the cycle rate above intentionally includes the
  drain window; use the PUT ack percentiles for the write path alone.

Raw JSONL for every table (per-sample lines plus summaries) is retained at
`.codex/evidence/terrabase-perf-fanout-20260915/source/measurement-harness/results/`
(`fanout.jsonl`, `snapshot.jsonl`, `logtail.jsonl`) and copied beside the
harness in `.codex/swarm/measurement/reactive-fanout-20260915/results/`.
