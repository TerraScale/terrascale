# Runtime commit boundary handoff

Source authority: live terrabase checkout at the handoff below. Frozen
qualification source remains `/tmp/terrabase-qual-fae32` and is unchanged.

The server boundary helper in `crates/terrabase-server/src/api.rs` checks
`Handle::try_current().runtime_flavor()`. It calls
`tokio::task::block_in_place` only for `RuntimeFlavor::MultiThread`, and calls
the same consuming `Transaction::commit` directly for current-thread and
non-Tokio callers. The four public document mutation callers, batch mutation,
and schema metadata commit route through this helper. Transaction ownership,
durability, replay, error mapping, and operation reservation lifetime are
unchanged.

Focused regression coverage adds real durable engine commits under Tokio
current-thread and multi-thread runtimes. No benchmark or Cargo build was run;
the benchmark owner owns the shared build target.

Live source state is intentionally dirty with concurrent work. Review the
targeted hunks in `api.rs` and `api_schema.rs` rather than applying a whole-tree
format or reset.
