# Capacity benchmark evidence

Every measured run has a Markdown table, raw client JSONL and aggregate JSON in
`<region>/<ULID>/`. Reports also land in `reports/<region>/`.

| Date | Evidence | Status |
| --- | --- | --- |
| 2026-11-07 | [TerraBase release build on hosted runners](2026-11-07-terrabase-ci-publish.md) | Push to TerraBase image 371 s to 140 s and 186 s; per-assembly ReadyToRun, 198 MB payload |
| 2026-11-05 | [Deleted shape CDN deadline (#1107)](2026-11-05-cdn1107.md) | 21/21 regional URLs 404 by 43.299 s; healthy-provider bound 420 s |
| 2026-11-05 | [Milestone B public-product acceptance](2026-11-05-milestone-b-acceptance.md) | **PASS pooled / FAIL SJC repeat 1**: independent regional latency, free reads, corrected home moves, CDN deletion, screenshots and full account cleanup |
| 2026-11-05 | [Fast suite budgets (#1084)](2026-11-05-fast-suite-budgets-1084.md) | All 12 admitted suites under 30 s; every case under 10 s |
| 2026-11-05 | [Regional shape tails and bounded CDC](2026-11-05-regional-shape-tails-and-cdc.md) | Regional tails proven; retention follow-up in progress |
| 2026-10-12 | [Replication baseline, local three regions, old engine](2026-10-12-repl-local-baseline.md) | 1M documents verified in every region; local, before design C |
| 2026-10-12 | [Cost of releasing abandoned Turso statements (#115), and of the native statement runner](2026-10-12-statement-drain.md) | Local, 2 CPUs (affinity + quota) and 512 MiB enforced on the server process; not a cloud measurement |
| 2026-10-12 | [Write-throughput floor](2026-10-12-write-floor-open-gate.md) | **OPEN release gate**: base 423 and R8 385 docs/s, both below 500 on the standard 2-SMT-thread stand-in (R8's controlled runs) |
| 2026-10-12 | [Backup and PITR at 1M, local S3 emulator (G0b)](2026-10-12-pitr-g0b-1m.md) | **Provisional, not Tigris**: pre-fence harness on `c379cc3b`; every Path A and Path B restore exact; reproduced backup defects listed in [the plan](../docs/pitr-100m.md) |
| 2026-10-13 | [Backup and PITR at 1M with the integrated fixes, local S3 emulator (G0b qualification)](2026-10-13-pitr-g0b-1m-integrated.md) | **GREEN, not Tigris**: `087a8e2b`; required 9/9 exact, 0 failures, every fault case typed or exact; restore memory still up to 6.7 GB |

Unit-test fixtures are temporary files and are never recorded as live runs.
