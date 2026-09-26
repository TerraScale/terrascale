# Active Rust benchmark evidence

The [v1 acceptance matrix](terrabase-rust-acceptance-v1.md) defines the required
cases and current status. Publish human-readable benchmark comparisons here as
Markdown tables. Keep the old .NET reports under
[`../history/`](../history/README.md); their measurements do not close Rust
acceptance rows.

Use `fixtures/<fixture-id>/` for a generator/seed/dataset manifest. Place new
matrix-qualified outputs under `<matrix-version>/<case-id>/<trial-id>/`,
including the raw JSONL samples, JSON run receipt, environment manifest, and
any profiles.
Use an immutable run ID so a rerun cannot overwrite evidence. A result table
links every trial receipt and states both source revisions, binary and fixture
hashes, environment, durability and cache mode, errors, metric distribution,
confidence interval, and scoreboard status. Preserve failed trials and negative
results. Historical and optional Firecracker development runs keep their
environment receipts in `firecracker-harness/<run-id>/`.

The `terra-rust` driver has SQLite and an initial embedded TerraBase caller.
Do not mark a comparison row passed until their semantics and paired trials
meet the matrix's registered resource and environment rules. The
[`https-document` driver](../https-document/README.md) writes process-based,
public HTTPS point-read diagnostics to `https-document-point-read/<run-id>/`.
Those receipts measure the client endpoint, including a TLS handshake per
request; they are not engine-only or SQLite-comparable measurements and do not
close a matrix row. CI builds and validates drivers on `terrascale-builder` as
processes, without running a benchmark or requiring a VM. Product Fast and Slow
integration results belong to TerraBase's own suites.
