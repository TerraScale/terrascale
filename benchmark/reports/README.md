# Regional capacity reports

The runner writes `<region>/<UTC-date>-<workload>.md`, with timestamp precision
and exclusive creation to prevent overwrites. Matching raw JSONL, aggregate JSON
and Markdown tables live under `bench/<region>/<ULID>/`.

No live report exists yet. See
[the prerequisites](../bench/2026-10-08-prerequisites.md). Synthetic fixtures
are confined to temporary unit-test directories.
