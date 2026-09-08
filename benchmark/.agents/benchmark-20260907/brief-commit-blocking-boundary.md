# Design brief: keep synchronous SQLite commit off Tokio worker execution

The current point benchmark's public writes reach synchronous
`Transaction::commit(LocalDurability::Synced)` from async server handlers;
representative callers are `crates/terrabase-server/src/api.rs:2940,4009,4217,4495`
and `api_schema.rs:101`. The commit path is intentionally synchronous and
owns an engine-borrowing `Transaction<'engine>` whose `Drop` releases the
operation reservation (`src/engine/mod.rs:4864-4892`). The active profile shows
these handlers blocking in `sqlite::Transaction::commit` while the SQLite
committer performs the durable fsync. This serializes offered HTTP concurrency
behind the writer and is a real write-lane performance candidate.

## Recommended bounded design

Add one server-side helper at the async boundary, used by the public mutation
callers that already fully stage and validate a transaction:

```text
commit_on_runtime(transaction, durability):
  if current Tokio runtime is multi-thread:
      tokio::task::block_in_place(|| transaction.commit(durability))
  else:
      transaction.commit(durability)
```

`block_in_place` is preferred to `spawn_blocking`: it accepts the borrowed
transaction without requiring a `'static` move, and commit remains the same
single ownership boundary. Tokio hands the blocked worker's capacity to its
blocking pool on the production multi-thread runtime. Current-thread tests
must take the direct branch because `block_in_place` panics there. Calls made
outside Tokio remain direct. Verify the exact Tokio runtime flavor API used by
the repository version before implementation.

Do not split staging from commit, clone or reconstruct the transaction, release
the operation reservation early, or use an unbounded custom thread. Preserve
the source-authority read barrier, lifecycle pause/revocation checks, durable
operation ledger and replay identity, atomic group commit, and `Synced` fsync
semantics. A commit error/uncertain result must map through each caller's
existing response and replay logic unchanged.

## Required call-path scope and checks

Start with the benchmarked public document/batch mutation handlers in `api.rs`
and their schema path; audit every async caller of `.commit(...)` before
selecting the final helper scope. Synchronous engine tests and background
workers remain direct unless they demonstrably execute on Tokio worker threads.
Existing repository patterns use `spawn_blocking` for owned closures (for
example `crates/terrabase-server/src/admission.rs:267-305`), but that pattern
cannot wrap this borrowed transaction after staging; do not force it through
unsafe ownership changes.

Acceptance cases must cover: multi-thread runtime commit success; current-thread
runtime commit success without panic; operation reservation release on commit
and drop; committed retry returns the original receipt; conflict/rejection and
unknown durability outcomes preserve existing HTTP mappings; pause/revocation
cannot cross the commit boundary; and batch atomicity remains intact. Use the
smallest existing focused selectors around API writes, batch replay,
`api_write_durability_tests`, conditional replay, and group commit. Bench_owner
owns the source-frozen before/after write lane; no load run is needed to decide
the API shape. Root/Astra must review whether the runtime helper should be
centralized before implementation because this crosses several handler files.
