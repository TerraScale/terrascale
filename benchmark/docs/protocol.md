# TS-761 capacity benchmark protocol

This is the shared contract for benchmark/, the Go/.NET/Rust SDK clients and
ts-orchestrator cluster metrics. It benchmarks the complete public TerraScale
path: SDK authentication/routing → deployed regional API → TerraBase. It does
not qualify an archived engine, a local process or an internal administrative
listener. No migrations, legacy data import or compatibility API-key workaround.

## Workload files

`workloads/<name>.json`:

```json
{
  "name": "mixed-70-30",
  "duration_s": 60,
  "warmup_s": 5,
  "concurrency": 8,
  "dataset": {"documents": 128, "doc_size_bytes": 1024},
  "mix": [
    {"op": "doc.get", "weight": 70},
    {"op": "doc.update", "weight": 30}
  ],
  "targets": {"p99_ms": 200}
}
```

Weights are positive integer percentages summing to 100, with no repeated op.
Supported ops: `doc.create`, `doc.get`, `doc.update`, `doc.delete`,
`query.simple`, `query.indexed`, `txn.multi`. An optional per-entry
`doc_size_bytes` overrides the dataset payload size for that operation. Size
means the exact UTF-8 document JSON byte count, excluding HTTP envelopes.

Initial workload names: point-reads, write-heavy, mixed-70-30, query, txn, ramp.
Bounds: measurement 1–600 seconds; warmup 0–60 seconds; concurrency 1–256;
documents 1–10,000; document size 32–1,048,576 bytes; p99 target 1–30,000 ms.
Reject unknown fields, trailing JSON values and invalid weights before network
calls. The workload JSON is stored with every run for reproducibility.

Ramp adds this explicit, language-neutral step configuration:

```json
"ramp": {"step_s": 15, "concurrency": [1, 2, 4, 8, 16, 32, 64]}
```

Steps strictly increase, begin at the base `concurrency`, each last at least
five seconds, and total `duration_s`. At most seven steps. Ramp finds the highest
successful completed step with p99 at the target and error rate ≤1%, and stops
on **any measured second** with error rate >1% or p99 >2× target. Earlier passing
steps remain valid observations when the next step trips the safety guard.
A stopped step is not a sustained passing step.
The error guard uses total errors divided by total arrivals across all operations
in that second; the latency guard checks each operation's p99 separately.

## Language CLI and resource lifecycle

Each implementation lives under `clients/<lang>/` (`go`, `dotnet`, `rust`).

The first-party operating contract requires automatic discovery of every
eligible serving region and local region inference, with no activation or
manual endpoint pinning. Current runner/preflight CLIs still require a closed
region selector; this is an implementation removal gap, not a serving allowlist
or the final setup contract. No automatic CLI/API is implemented by this docs
change. Qualification remains bounded to its authorized test allocation.

Resolve endpoints through the official SDK's authenticated discovery, retaining
customer scope, placement/residency and TLS validation. Authentication uses
the SDK's ts-auth client-credentials flow (JWT). First-party production
credentials must be created/delivered automatically, while vendor-issued keys
are stored once and picked up automatically. Delivery integration remains
pending; no manual credential wiring belongs in first-party onboarding.
Credentials must authorize a dedicated benchmark project and database/document
read/write scopes. An unscoped service JWT is not tenant authority. No fake
data key, header-swapping transport, internal calls, loopback admin key or
legacy membership/key-management route.

The SDK creates one disposable database named `bench-<run-id>`, homed in the
discovered eligible region within the authorized test allocation, then
collections, seeds and an active index. Customer placement is an authorization
constraint, not a region activation step. Every entity ID
is a plain canonical ULID: uppercase Crockford base32, 26 characters, first
character 0–7, no prefix. The database **name** has the required `bench-` prefix;
its database **ID** does not. Every run owns only its own disposable database.
The persistent `benchmark` result database is never deleted by client cleanup.

Setup and warmup are excluded from measured counts, histogram and throughput.
Warmup runs once per client before its first measured phase, including ramps.
Cleanup deletes the owned database on success, setup failure, safety stop,
SIGINT and SIGTERM, using a fresh bounded context. Emit the final summary only
after cleanup; record `cleanup:false` if deletion fails. Do not overwrite an
existing JSONL file. Never print secrets or tokens. No retry of measured SDK
operations: retries would hide capacity failures and duplicate uncertain writes.

One run is bounded to 15 minutes including setup and cleanup. Allow five seconds
per public request and five seconds of final drain; reserve 30 seconds for
cleanup. Only context-respecting SDK callbacks may be passed to the measurement
engine. Live runs in gru/sfo are authorized; `mariogk-desktop` is excluded.

### Operation semantics

Use immutable read/query seeds and a separate transient write collection so a
mixed write workload cannot erase its own read dataset. Validate returned
identity, seeded fields, query results and transaction receipt through the SDK.
Point update replaces an existing seed without removing the fields used for
validation. Create uses a fresh ULID. Delete targets a freshly created transient
document; its latency includes both public calls, and it counts as one logical
operation. `txn.multi` writes two distinct ULIDs atomically and counts as one
logical operation. Query workloads use matching seed collections: one without
an explicit index and one with an index confirmed active before warmup. A query
is one bounded page (limit 10) with the same equality predicate in both cases.
Report logical operations/s, not HTTP requests/s.

## Scheduling, latency and JSONL

Use an absolute monotonic open-loop schedule. Offered rate per step is
`min(10000, max(1, round(concurrency * 1000 / targets.p99_ms)))` logical ops/s,
with positive values rounded half up. Allocate arrivals independently of
completion; permit at most `concurrency` outstanding SDK operations. Slots missed
by more than 10 ms and arrivals rejected at the concurrency limit count as
errors. Do not queue unbounded work or burst to recover a paused scheduler.
Choose the operation by `(arrival_index % 100 * 37) % 100`, walking cumulative
weights. This spreads the mix while preserving exact weights every 100 slots,
even when some operations are dropped. Reset arrival index at each phase.

Correct coordinated omission by recording successful completion time minus
**intended scheduled start time**, including dispatch delay. Failed/dropped
arrivals count toward error rate and never enter successful latency histograms.
A report with >1% errors fails; errors cannot silently improve its verdict.
Use an HDR-style histogram: microsecond units, minimum 1 μs, maximum 900,000,000
μs, three significant digits. Histogram latency precision is approximate;
quantiles must be computed from merged histograms, never averaged.

Emit one line per measured second per configured operation (including zero
counts), with an optional final partial second. `ts` is the UTC RFC3339 timestamp
at the start of the completion bucket. Every line includes:

```json
{"ts":"2026-10-08T20:00:00Z","client":"go","sdk":"ts-go","sdk_version":"<pinned-version>","region":"gru","op":"doc.get","count":40,"errors":0,"p50_us":1000,"p95_us":2000,"p99_us":3000,"max_us":4000,"step":0,"concurrency":8,"elapsed_s":1}
```

`count` includes all attempted arrivals, including errors and admission drops;
`errors/count` is error rate. Window latencies describe successful completions.
Assign each completion to its recorded end timestamp's bucket, including records
queued during slow output. Finalize a bucket only after accounting for all
completion records preceding its boundary. Refresh the clock after output before
checking missed slots or admitting new arrivals.
`step`, `concurrency` and `elapsed_s` are additive shared fields: clients must
emit them for validated runner aggregation. Drain completions remain in the
run's measured window and denominator. Windows are unique by `(ts, op)` per
client output. A truncated or malformed stream never receives a capacity verdict.

Exactly one final summary follows all windows:

```json
{"type":"summary","client":"go","region":"gru","workload":"point-reads","workload_config":{"name":"point-reads","duration_s":60,"warmup_s":5,"concurrency":4,"dataset":{"documents":1000,"doc_size_bytes":1024},"mix":[{"op":"doc.get","weight":100}],"targets":{"p99_ms":100}},"ops_per_s":40,"error_rate":0,"per_op":{"doc.get":{"count":40,"errors":0,"p50_us":1000,"p95_us":2000,"p99_us":3000,"max_us":4000,"histogram":"<object described below>"}},"run_id":"<ULID>","start":"<UTC RFC3339>","end":"<UTC RFC3339>","elapsed_s":60,"steps":[],"cleanup":true}
```

The example shows field names; actual `steps` must be nonempty and contain
`concurrency`, `start`, `end`, `elapsed_s`, `ops_per_s`, `error_rate`, `per_op` and
`completed`. `stopped` optionally names a safety/cancellation failure. The exact
ramp guard reason is `ramp safety threshold exceeded`. Summary and step counts
must agree with windows and with one another. Throughput is successful logical
operations divided by measured wall-clock seconds including drain. A completed
step must have its full configured duration; incomplete runs do not pass.
Each window's entire interval must lie within its assigned step, including a
final partial or drain bucket. A ramp with no stop reason must include every
configured step and complete each one. A safety-stopped ramp must have an
incomplete final step preceded only by completed steps.

`workload_config` is required and contains the complete measured workload object,
including dataset sizes, mix weights and document size overrides, warmup, target
and ramp configuration. The runner compares every typed field with its requested
workload before accepting a stream; mix and ramp array order must match. JSON
object key order and omitted zero-valued optional fields do not affect equality.
Missing configuration is rejected; the workload name alone is not identity.

For each operation's step and final summary, `histogram` is the dense HDR
snapshot object with `LowestTrackableValue:1`,
`HighestTrackableValue:900000000`, `SignificantFigures:3`, and `Counts`, the
nonnegative int64 counts array in standard HDR count-index order. This is the
`hdrhistogram-go` snapshot layout; other clients must export equivalent bucket
geometry. The histogram's total count equals `count-errors`, and its computed
quantiles must equal the accompanying fields. Window histograms are omitted to
keep JSONL small. These additive summary fields permit exact histogram merging
across SDK languages while preserving the base summary contract.

## Runner, metrics, persistence and capacity verdict

The runner invokes conforming clients, retains their output and validates it
before aggregation. Its current closed region selector remains an implementation
removal gap; the operating contract uses automatically discovered eligible regions.
Repeat `--client` for up to three languages. The current local runner invokes
them sequentially under one deadline; repeat `--input` to aggregate separately
collected outputs. ts-orch load-generator jobs may supply the same output in a
later execution lane without changing the protocol.
Before creating a run directory or launching any client, reject combinations
where `number_of_clients * (duration_s + warmup_s)` exceeds the shared 870-second
execution budget. Ramp `duration_s` already includes every configured step.
Setup, drain and cleanup also consume that budget; the lower-bound check does
not guarantee completion. The final 30 seconds of the 15-minute bound allow
forced child termination. Imported `--input` streams do not execute clients
and are not subject to this scheduling check.

Merge per-op HDR buckets and counters. Throughput uses successes divided by the
union's outer start/end bounds, so sequential runs include intervening gaps and
cannot be treated as parallel load. Reject duplicate client run IDs, wrong
regions/workloads, summary/window count disagreements and invalid histograms.
The current capacity step value is the highest observed **individual-client**
passing step, not a summed multi-generator cluster ceiling.
For multiple ramp clients, every run must have a completed passing step, successful
cleanup and either a complete ramp or its own explicit terminal safety stop.
An unacceptable interruption or missing passing observation in any client fails
the aggregate.

Attach cluster CPU and memory for the exact UTC measured window using the
**actual ts-orchestrator metrics API** after its TS-796 owner admits it.
That dependency belongs to TS-796; TS-795 must neither remove it nor invent
a replacement. Do not invent a route or collect shell metrics from hosts instead. Until then, store
`cluster_metrics:{status:"unavailable",reason:"...",start:"...",end:"..."}`.
Never substitute zeros, estimate host utilization or claim saturation from
missing metrics.

Persist through public SDK calls in TerraBase database `benchmark`, in a
benchmark project in the discovered eligible region, subject to customer
placement/residency authorization. Creating that tenant database does not
activate a serving region.
Collections: `runs` (workload, interval and lifecycle), `results` (per-second
operation rows), `summaries` (each client summary with HDR), `cluster_metrics`
(available samples or explicit unavailable record), `reports` (aggregate and
verdict). Entity primary keys are plain ULIDs; documents also carry `run_id`.
Write a `persisting` run before child entities; finalize `complete` only after
all child writes succeed. Partial persistence never appears as a completed run.
A persistence error fails the runner while retaining local evidence. Retries
must not overwrite or duplicate an uncertain result.

Markdown tables live in both `bench/<region>/<run-id>/summary.md` and
`reports/<region>/<UTC-timestamp>-<workload>-<report-id>.md`; the report ID is a
plain ULID generated for each runner invocation, including repeated aggregation
of the same input files. Published reports use exclusive creation to preserve
earlier evidence. Keep raw JSONL and aggregate JSON
beside the benchmark table. Never commit synthetic test data as a live result.
Reports identify SDK/version, client run IDs, workload, wall-clock boundaries,
errors, per-op p50/p95/p99/max, passing ramp steps, cleanup status, persistence
status and cluster metrics availability.
When the runner invokes clients, reports include `client_execution` keyed by
client run ULID (`success`, `failed`, or `safety-stop`). An unexpected nonzero exit
forces the saved JSON and Markdown verdicts to FAIL. Only that client's own
recorded ramp safety stop permits an expected nonzero exit. Process status is
unavailable when aggregating pre-existing input files.

A fixed workload passes only when every configured operation has successful
samples, aggregate p99 ≤ target, errors ≤1%, all phases complete and cleanup
succeeds. Ramp reports the highest completed passing step; an additional safety
stop can still preserve that observation. This is an observed sustained rate at
the offered load, not proof of the cluster's absolute maximum capacity. With
metrics unavailable the report explicitly lacks a CPU/memory saturation claim.

## Slice 1 implementation status

Implemented: six workloads, validation, bounded open-loop measurement with HDR,
JSONL validation/merging, local runner and Markdown reporting. The normal persistence
path fails after local reporting until actual SDK persistence is implemented.
The existing evidence-only storage bypass is inventoried for removal, not an
approved production mode or successful capacity qualification.

Blocked: the Go SDK lacks public JWT project data-client integration, so no
conforming Go client, TerraBase result persistence or gru/sfo capacity numbers
are claimed. [Producer evidence](../clients/go/README.md). Slice 4 is not yet
available; metrics are unavailable as required. Other clients build against the
contract above, not the retired acceptance tooling.
