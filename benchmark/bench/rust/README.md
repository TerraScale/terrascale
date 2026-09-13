# Historical Rust benchmark results

This directory is the archive for benchmark work run against the former Rust
TerraBase implementation. It is separate from `bench/`, which is reserved for
benchmarks of the current C# TerraBase host.

The original result filenames and JSON contents are preserved. A receipt whose
JSON target is `terrabase` means the Rust TerraBase member when it appears in
this archive; the archive path and this document provide the implementation
identity that the old generic filenames did not carry. Comparator receipts
(PostgreSQL, RavenDB, SQLite, Turso, and ZoneTree) are retained alongside the
Rust receipts because they belong to the same historical benchmark campaigns.

- [`RESULTS.md`](RESULTS.md) — historical rendered tables and admission notes.
- [`results/`](results/) — retained campaign receipts and run artifacts.
- [`results-baseline-20260909/`](results-baseline-20260909/) — the baseline
  snapshot used by the 2026-09-09 re-run.
- The other Markdown files are the campaign reports and engineering probes
  produced with those receipts.

The old location was `bench/`. Paths in raw logs are historical evidence and
are intentionally left unchanged; use this directory as the canonical archive
location when opening a retained artifact today.
