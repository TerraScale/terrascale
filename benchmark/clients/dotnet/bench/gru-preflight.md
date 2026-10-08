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
