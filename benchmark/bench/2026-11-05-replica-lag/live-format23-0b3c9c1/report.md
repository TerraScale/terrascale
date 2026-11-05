# Format-23 live replica lag, 2026-11-05

Acceptance is incomplete. The first FRA repeat stopped at write 47 with HTTP 409 `serialization_conflict` from the GRU SQL endpoint. FRA repeats 2–3 and all SJC repeats did not run. The account had one sequential writer; the benchmark did not retry this 409.

All three regions ran `0b3c9c147d83df07fd6fbe58456ca004892fb757`, including the shipped WAN lock fix, on format 23. Deployment and benchmark hashes are in `deployment.json`. The strict benchmark rejects successful reads forwarded home and requires an attributed revision notice for each completed sample.

| Read region | Repeat | Completed writes | Local | Pushed pages | Commit→visible p99 ms | Result |
|---|---:|---:|---:|---:|---:|---|
| fra | 1 (partial) | 46 | 46 | 46 | 282.096 | FAIL / incomplete |

The partial percentile is diagnostic only. It is not a 500-write acceptance cell.

Write 46 was sent at 15:32:22.372647 UTC, acknowledged at 15:32:22.563864 and visible locally at 15:32:22.584347. Write 47 followed immediately and failed. The benchmark did not record the failed request's start/end timestamps or response message. Database cleanup also returned HTTP 503 `storage_busy`.

The retained JetStream KV record confirms that this database's home lease was written at **15:32:22.800329175 UTC**, sequence 9666 (`home-lease-record.json`). That is 216 ms after write 46 became visible, during the failure interval: the run log closed by 15:32:23.075 UTC. The read-only query used `last_by_subj` on `$KV.terrabase-homes.home.01M6P5DHXGD23RDC9CP319CDVD` in `KV_terrabase-homes`; no lease state was changed.

This matches GRU's 15-second home-lease scan phase. GRU first logged a home lease session at 15:24:07.927591 UTC; the scan timer starts during the same service initialization. Its exact start and scan entry were not logged, but the KV timestamp establishes the actual lease confirmation time. A later lease lapse at 15:28:09.672398 and new session at 15:28:12.205912 do not restart that timer.

The server has a matching refusal path: `HomeLeases.ConfirmAsync` adds a newly discovered replicated database to `HomeLeaseGate` with `held:false` before confirming it over NATS KV. Previously unregistered databases pass the gate. While confirmation is pending, `HomeLeaseGate.ThrowIfUnleased` throws `StorageBusyException` at transaction begin or commit; SQL maps a busy transaction begin to HTTP 409 `serialization_conflict`. The KV timestamp and source path support this server-fencing cause; the failed request's exception stack was not captured. The fast-writes owner received the timestamps and owns the shared lease-scan fix. No client conflict retry was added.

The throwaway account `lag-format23-1105` was deleted after two pending-cleanup retries. Both remote private credential files were removed and their absence checked. Raw JSONL and runtime logs are gzip compressed; they contain timing and routing evidence without credentials. Clock and orchestrator start snapshots are retained alongside them.
