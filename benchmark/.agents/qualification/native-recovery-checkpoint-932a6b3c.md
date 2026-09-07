# Focused native recovery checkpoint verification

- Revision tested: `932a6b3c0716ed310f5537b739db9416dceb330b`
- Command: `nice -n 19 cargo test -p terrabase-server native_recovery::checkpoint --lib --locked`
- Result: **FAIL**, exit 101; 12 passed, 1 failed, 489 filtered out.
- Log: [native-recovery-checkpoint-932a6b3c.log](native-recovery-checkpoint-932a6b3c.log)

Failing test:

```text
native_recovery::checkpoint::tests::capturing_checkpoint_retry_enters_measuring_without_stale_error
thread ... panicked at crates/terrabase-server/src/native_recovery/checkpoint/tests.rs:131:6:
called `Result::unwrap()` on an `Err` value: VerificationFailed
```

The discriminating runtime path is `crates/terrabase-server/src/native_recovery/checkpoint.rs`: `advance` immediately returns `VerificationFailed` when `state.phase` is `Capturing` (the test supplies a durable checkpoint and expects transition to `Measuring`). All other checkpoint tests passed, including SQLite reopen/resume and complete replay validation.

The worktree had concurrent edits in `native_recovery.rs`, `checkpoint/tests.rs`, and `src/storage/sqlite/storage_closure.rs`; this lane made no source edits.
