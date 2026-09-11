# API import clippy slice

Applied the approved safe-mechanics slice to
`crates/terrabase-server/src/api_imports.rs`:

- checked millisecond conversion now preserves pre-epoch zero and saturates
  positive overflow at `u64::MAX`;
- clarified mutex construction and unit-valued error patterns;
- changed replay selection to `map_or_else` and merged only identical
  `Unknown` outcome arms;
- used `let ... else` for key/upload admission with the same refusal responses;
- collapsed cancellation cleanup and queued-job discovery conditions without
  changing durable cursor, pending-operation, or cleanup behavior.

The conversion and pattern changes were checked against concrete return types;
only `document_scope` uses `map_err(|()| ...)` because it returns `Result<_, ()>`.

Validation:

```text
rustfmt --edition 2024 --check crates/terrabase-server/src/api_imports.rs
git diff --check -- crates/terrabase-server/src/api_imports.rs
```

Cargo compile/tests remain assigned to the consolidated clippy owner.
