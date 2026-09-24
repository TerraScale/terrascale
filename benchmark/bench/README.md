# Active Rust benchmark evidence

The [v1 acceptance matrix](terrabase-rust-acceptance-v1.md) defines the required
cases and current status. Publish human-readable benchmark comparisons here as
Markdown tables. Keep the old .NET reports under
[`../history/`](../history/README.md); their measurements do not close Rust
acceptance rows.

Use `fixtures/<fixture-id>/` for a generator/seed/dataset manifest. Place new
guest outputs under `<matrix-version>/<case-id>/<trial-id>/`, including the raw
JSONL samples, JSON run receipt, guest environment manifest, and any profiles.
Use an immutable run ID so a rerun cannot overwrite evidence. A result table
links every trial receipt and states both source revisions, binary and fixture
hashes, environment, durability and cache mode, errors, metric distribution,
confidence interval, and scoreboard status. Preserve failed trials and negative
results. The Firecracker harness owns its environment receipts in
`firecracker-harness/<run-id>/`.

The initial `terra-rust` driver emits SQLite-only evidence. Do not mark a
comparison row passed until the real embedded TerraBase caller has equivalent
semantics and a paired guest run under the matrix's resource and trial rules.
