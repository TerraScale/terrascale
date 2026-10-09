# GRU-selected .NET CLI preflight — TS-761 Slice 2

**Not a live benchmark.** The client exits before reading credentials, sending
SDK requests or creating a database. No deployed gru service was contacted.
Throughput, error rate and p50/p95/p99/max latency are **unmeasured**.

- Host: `delta`; excluded host `mariogk-desktop` was not used.
- SDK: official `ts-dotnet` main `a4ff285c051e63e72cb01c4a94173c91da1dd69a`, project reference, .NET 10.0.401.
- Workloads: exact files from Slice 1 `4d001d6c33db45722ab5e66a3ebee61e6d95d949`; selected region `gru`.
- API requests: **0**; measured operations: **0**; created databases: **0**.

| Workload | Exit | Results JSONL |
| --- | --- | --- |
| point-reads | 1 | not created |
| write-heavy | 1 | not created |
| mixed-70-30 | 1 | not created |
| query | 1 | not created |
| txn | 1 | not created |
| ramp | 1 | not created |

Observed at 2026-10-08T23:35:41Z.

Each invocation was bounded to 10 seconds and ran at `nice -n 19`. All six
workloads validated and reached `SDK_BLOCKED`; no output was opened or
replaced. The launched executable is the checked-in `bench-client` launcher.

The pinned SDK lacks canonical project-JWT database create/delete and index
APIs. Its legacy lifecycle methods do not satisfy this ticket. Full scheduling,
HDR, per-second JSONL, summary, ramp execution and live cleanup are unimplemented.
A real bounded gru receipt requires those public SDK prerequisites and dedicated
project-scoped client credentials from OCI Vault. Do not use this table as a
capacity or latency result.

## Merged Slice 1 runner preflight

Observed at 2026-10-09T06:18:01+00:00 on `delta`, after rebasing onto main `85e757d8`.
The six checked-in workloads validate against the final merged protocol.

| Workload | Client exit | Results JSONL |
| --- | --- | --- |
| point-reads | 1 (`SDK_BLOCKED`) | not created |
| write-heavy | 1 (`SDK_BLOCKED`) | not created |
| mixed-70-30 | 1 (`SDK_BLOCKED`) | not created |
| query | 1 (`SDK_BLOCKED`) | not created |
| txn | 1 (`SDK_BLOCKED`) | not created |
| ramp | 1 (`SDK_BLOCKED`) | not created |

The merged runner invoked the .NET launcher for point-reads and exited 1,
retaining FAILED receipt `01M4FMZEVVFW66DJ6G97TQCRSE` with throughput, latency and
cluster metrics unavailable and `stored:false`. No client JSONL was created.
This confirms CLI integration and rejection of an unmeasured run. It is
not a live benchmark: zero API requests, measured operations or databases.
Every invocation ran at `nice -n 19`; each client harness had a 10-second
bound and the runner harness had a 12-second bound.
