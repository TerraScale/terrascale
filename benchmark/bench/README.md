# Capacity benchmark evidence

Every measured run has a Markdown table, raw client JSONL and aggregate JSON in
`<region>/<ULID>/`. Reports also land in `reports/<region>/`.

| Date | Evidence | Status |
| --- | --- | --- |
| 2026-10-08 | [SDK and live-run prerequisites](2026-10-08-prerequisites.md) | Blocked; no capacity number claimed |
| 2026-10-11 | [Turso spike write throughput](2026-10-11-turso-spike.md) | Engine micro-benchmark on Fly gru; not capacity evidence |
| 2026-10-12 | [Replication baseline, local three regions, old engine](2026-10-12-repl-local-baseline.md) | 1M documents verified in every region; local, before design C |
| 2026-10-12 | [Cost of releasing abandoned Turso statements (#115), and of the native statement runner](2026-10-12-statement-drain.md) | Local, 2 CPUs (affinity + quota) and 512 MiB enforced on the server process; not a Fly machine |
| 2026-10-12 | [Write-throughput floor](2026-10-12-write-floor-open-gate.md) | **OPEN release gate**: base 423 and R8 385 docs/s, both below 500 on the standard 2-SMT-thread stand-in (R8's controlled runs) |
| 2026-10-13 | [Three-region live replication on Fly, disposable app](2026-10-13-three-region-live/README.md) | EXPLORATORY. Latest: pre-freeze e1812ce3 at production size (shared-cpu-2x 512 MB, no throttling): runbook r2 genesis without an operator, six directions, suites, partition with conflicts, kills/restarts, F7 and F3 live proofs and a non-empty PITR all pass; E9 add-wins regression (B1) and version-token divergence (B2) found |
| 2026-10-12 | [Backup and PITR at 1M, local S3 emulator (G0b)](2026-10-12-pitr-g0b-1m.md) | **Provisional, not Tigris**: pre-fence harness on `c379cc3b`; every Path A and Path B restore exact; reproduced backup defects listed in [the plan](../docs/pitr-100m.md) |
| 2026-10-13 | [Backup and PITR at 1M with the integrated fixes, local S3 emulator (G0b qualification)](2026-10-13-pitr-g0b-1m-integrated.md) | **GREEN, not Tigris**: `087a8e2b`; required 9/9 exact, 0 failures, every fault case typed or exact; restore memory still up to 6.7 GB |

Unit-test fixtures are temporary files and are never recorded as live runs.

TS-832 API benchmark reports (not capacity evidence) land in [results/](results/).
