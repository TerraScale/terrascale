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
| 2026-10-13 | [Three-region live replication on Fly, disposable app](2026-10-13-three-region-live/README.md) | EXPLORATORY, candidate 9667b7ef (not main): six directions, suites, 3,000-doc counts/digests through kills and restarts, and a real ip6tables partition with rejoin all pass on performance-1x; PITR not run yet; F3–F8 found (F7: hard stop lost anchored catalog commits); production sizing note |

Unit-test fixtures are temporary files and are never recorded as live runs.

TS-832 API benchmark reports (not capacity evidence) land in [results/](results/).
