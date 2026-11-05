# Format-23 live replica lag, 2026-11-05

Acceptance is incomplete. The first FRA repeat stopped at write 47 with HTTP 409 `serialization_conflict` from the GRU SQL endpoint. FRA repeats 2–3 and all SJC repeats did not run. The account had one sequential writer; the benchmark did not retry this 409.

All three regions ran `0b3c9c147d83df07fd6fbe58456ca004892fb757`, including the shipped WAN lock fix, on format 23. Deployment and benchmark hashes are in `deployment.json`. The strict benchmark rejects successful reads forwarded home and requires an attributed revision notice for each completed sample.

| Read region | Repeat | Completed writes | Local | Pushed pages | Commit→visible p99 ms | Result |
|---|---:|---:|---:|---:|---:|---|
| fra | 1 (partial) | 46 | 46 | 46 | 282.096 | FAIL / incomplete |

The partial percentile is diagnostic only. It is not a 500-write acceptance cell.

Write 46 was sent at 15:32:22.372647 UTC, acknowledged at 15:32:22.563864 and visible locally at 15:32:22.584347. Write 47 followed immediately and failed. The benchmark did not record the failed request's start/end timestamps or response message. Database cleanup also returned HTTP 503 `storage_busy`.

The failure overlaps the expected phase of GRU's 15-second home-lease scan. GRU first logged a home lease session at 15:24:07.927591 UTC; the scan timer starts during the same service initialization. Its exact start and scan execution times were not logged, so the phase is inferred, not measured. A later lease lapse at 15:28:09.672398 and new session at 15:28:12.205912 do not restart that timer.

The server has a matching refusal path: `HomeLeases.ConfirmAsync` adds a newly discovered replicated database to `HomeLeaseGate` with `held:false` before confirming it over NATS KV. Previously unregistered databases pass the gate. While confirmation is pending, `HomeLeaseGate.ThrowIfUnleased` throws `StorageBusyException` at transaction begin or commit; SQL maps a busy transaction begin to HTTP 409 `serialization_conflict`. This is a candidate server cause, not evidence of concurrent user writes. The fast-writes owner received the timestamps and owns the shared lease-scan investigation. No client conflict retry was added.

The throwaway account `lag-format23-1105` was deleted after two pending-cleanup retries. Both remote private credential files were removed and their absence checked. Raw JSONL and runtime logs are gzip compressed; they contain timing and routing evidence without credentials. Clock and orchestrator start snapshots are retained alongside them.
