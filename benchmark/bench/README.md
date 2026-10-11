# Capacity benchmark evidence

Every measured run has a Markdown table, raw client JSONL and aggregate JSON in
`<region>/<ULID>/`. Reports also land in `reports/<region>/`.

| Date | Evidence | Status |
| --- | --- | --- |
| 2026-10-08 | [SDK and live-run prerequisites](2026-10-08-prerequisites.md) | Blocked; no capacity number claimed |
| 2026-10-11 | [Turso spike write throughput](2026-10-11-turso-spike.md) | Engine micro-benchmark on Fly gru; not capacity evidence |

Unit-test fixtures are temporary files and are never recorded as live runs.

TS-832 API benchmark reports (not capacity evidence) land in [results/](results/).
