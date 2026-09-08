# Execution brief: unblock shard split release compilation

Outcome: compile the current `terrabase` release artifact so benchmark_owner
can run the final TerraBase lanes against the intended source. The compiler
error is at `src/engine/shard_split.rs:835`:

```rust
identity.database_incarnation != incarnation || identity.region != *self.database_owner().region()
```

`DatabaseOwner::region()` is `pub const fn region(&self) -> RegionCode` and
`RegionCode` is `Copy` (`src/storage/ownership.rs:73-152`, `src/regions.rs:49`).
It returns a value, so unary dereference is invalid. The bounded correction is
to compare `identity.region` directly with `self.database_owner().region()`.

Ownership: one worker owns only `src/engine/shard_split.rs:835` and any focused
regression evidence it creates; do not edit `AGENTS.md`, benchmark scripts,
results, or unrelated shard split logic. No architecture checkpoint is needed:
this is a type-correctness fix with an established accessor contract.

Acceptance: current `terrabase` source compiles past this error; run the
existing shard split/file identity focused tests selected by the build owner,
then hand the exact source hash and release artifact digest to bench_owner.
Do not accept a test-only artifact or the older `/tmp/terrabase-benchmark-20260907`
binary as evidence. The shared Cargo target is already in use, so submit the
selector and source handoff to the single build owner rather than starting a
competing build. Any new error outside this line is a separate brief.
