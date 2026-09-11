# Native backup shutdown hang — current diagnosis

Source cohort: `185ba400` (before any fix).

## Reproduction

```text
timeout --signal=TERM 45s nice -n19 env \
  CARGO_TARGET_DIR=/tmp/terrabase-native-g1-target.lNjLFz \
  CARGO_BUILD_JOBS=2 \
  cargo test -p terrabase-server --lib \
  native_backup_runtime::restore_tests::shutdown_waits_for_in_flight_provider_tick \
  --locked -- --nocapture
```

Result: exit `124`; test entered but did not complete. The same reproduction
using the transferred target reached `running 1 test` and timed out.

## Narrowed cause

Temporary tracing showed the test reaches fixture setup and supervisor spawn,
then repeatedly executes `supervise → tick → async gate → fleet lock` without
ever reaching `BlockingStore::put_opts`. Therefore it is blocked at
`store.entered.notified().await`; the shutdown assertions are never reached.
This is not evidence that shutdown cancels an in-flight provider operation.
No runtime or test contract change was made.

## Current source hashes after removing temporary tracing

```text
f38dfc3ce5b7999ab67b8994144ff652514c4328c74f54248e1c563b9ac2b668  crates/terrabase-server/src/native_backup_runtime.rs
104c59803aef7951b04f87f8166d07196780167cfe8ab3a5b78733247cd0ea4c  src/backup/worker.rs
```

Formatting check: `cargo fmt --all --check` passed. No code fix is currently
claimed; the next investigation must determine why the fixture's first tick
is not selecting a provider upload (likely routed/paused admission state).
