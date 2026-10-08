# TS-761 Rust GRU preflight — 2026-10-08

These are local fail-closed invocations selecting `gru`, **not live benchmarks**.
The SDK lacks APIs needed to create, measure and safely delete the required
throwaway dataset. No service was contacted and no Vault credential was read
or benchmark client provisioned. Host: `beta`; no desktop access.

## Shared workload compatibility

Workload files were read directly from Slice 1 draft revision `e8c22b292c2c547ea2b275e1cb4a8b9d78996303`.
Each file passed Rust schema/semantic validation and reached the SDK guard.
Started (UTC): 2026-10-08T20:49:36.826628+00:00. Formatting, strict Clippy and 10 local tests pass.

| Shared workload | Result | Preflight elapsed ms | Measured operations | Output created |
| --- | --- | --- | --- | --- |
| point-reads | SDK_BLOCKED (exit 1) | 65.528 | 0 | no |
| write-heavy | SDK_BLOCKED (exit 1) | 29.945 | 0 | no |
| mixed-70-30 | SDK_BLOCKED (exit 1) | 43.056 | 0 | no |
| query | SDK_BLOCKED (exit 1) | 34.405 | 0 | no |
| txn | SDK_BLOCKED (exit 1) | 34.385 | 0 | no |
| ramp | SDK_BLOCKED (exit 1) | 45.552 | 0 | no |

No throughput, latency, benchmark error rate or capacity verdict was measured.

## Reproducible short invocation

| Field | Observed value |
| --- | --- |
| SDK contract checked by tests | terrascale 0.1.0, `ded5adb3b63ce4af0e3a26f95139968cdf539501` |
| Selected region / workload | gru / point-reads |
| Requested measured / warmup seconds | 5 / 1 |
| Requested concurrency / documents / bytes | 1 / 10 / 128 |
| Mix | doc.get, weight 100 |
| Preflight elapsed ms | 42.652 |
| Exit status | 1 (`SDK_BLOCKED`) |
| Network requests / database changes / measured operations | 0 / 0 / 0 |
| JSONL lines / output file created | 0 / no |
| Throughput / latency / error rate | N/A — measurement never started |

Reproduce from the repository root (after building):

```sh
nice -n 19 clients/rust/target/debug/bench-client run \
  --workload clients/rust/bench/gru-preflight-workload.json \
  --region gru --out /tmp/ts761-rust-gru-results.jsonl
```

Stderr:

```text
SDK_BLOCKED: terrascale 0.1.0 @ ded5adb3b63ce4af0e3a26f95139968cdf539501: plain ULID document/project IDs (the SDK requires UUIDs); JWT project-scoped database provisioning and deletion (create uses pre-TS-589 PUT; no public database delete). See clients/rust/README.md. No credentials read, network requests, database changes or results emitted.
```

[SDK follow-ups](../README.md#sdk-follow-ups) block a compliant live run. The
load scheduler, histogram, JSONL output and ramp remain unimplemented.
