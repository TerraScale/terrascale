# Regional capacity reports

The runner writes `<region>/<UTC-date>-<workload>.md`, with timestamp precision
and exclusive creation to prevent overwrites. Matching raw JSONL, aggregate JSON
and Markdown tables live under `bench/<region>/<ULID>/`.

No live report exists yet. See
the prerequisites (retired evidence removed). Synthetic fixtures
are confined to temporary unit-test directories.
