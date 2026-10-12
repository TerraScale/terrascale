# Capacity benchmark evidence

Every measured run has a Markdown table, raw client JSONL and aggregate JSON in
`<region>/<ULID>/`. Reports also land in `reports/<region>/`.

| Date | Evidence | Status |
| --- | --- | --- |
| 2026-10-08 | [SDK and live-run prerequisites](2026-10-08-prerequisites.md) | Blocked; no capacity number claimed |
| 2026-10-11 | [Turso spike write throughput](2026-10-11-turso-spike.md) | Engine micro-benchmark on Fly gru; not capacity evidence |
| 2026-10-12 | [Replication baseline, local three regions, old engine](2026-10-12-repl-local-baseline.md) | 1M documents verified in every region; local, before design C |
| 2026-10-12 | [Cost of releasing abandoned Turso statements (#115)](2026-10-12-statement-drain.md) | Local, 2 CPUs (affinity + quota) and 512 MiB enforced on the server process; not a Fly machine |

Unit-test fixtures are temporary files and are never recorded as live runs.

TS-832 API benchmark reports (not capacity evidence) land in [results/](results/).
