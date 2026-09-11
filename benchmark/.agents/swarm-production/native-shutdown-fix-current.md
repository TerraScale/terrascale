# Native backup shutdown hang — current diagnosis

Source cohort: `185ba400` (before the fixture correction).

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

## Correction and acceptance

The supervisor fixture now wraps the real retained `MemberHost` in a
test-local `BackupEngineHandle` whose default `admitted_shard_read_cut` is
`None`. This intentionally selects the standalone provider path; production
`AdmittedBackupEngine` still fails closed on an invalid routed cut. The real
`NativeBackupRuntime::tick` performs the provider operation, and the test
verifies that supervision remains pending until the provider is released,
then drains the host lease.

```text
nice -n19 env CARGO_TARGET_DIR=/tmp/terrabase-native-g1-target.lNjLFz \
  CARGO_BUILD_JOBS=2 cargo test -p terrabase-server --lib \
  native_backup_runtime::restore_tests::shutdown_waits_for_in_flight_provider_tick \
  --locked -- --nocapture
```

Result: exit `0`; `1 passed`, `0 failed`, `604 filtered out`; test time `0.60s`
(build time `55.51s`).

Adjacent lifecycle regression also passed with the same isolated target:
`pre_signalled_supervisor_does_not_tick_and_drain_releases_host_lease` — exit
`0`; `1 passed`, `0 failed`, `604 filtered out`; test time `0.64s`.

## Current source hashes after removing temporary tracing and applying the
## fixture correction

```text
25b3843ef46f53cdc07331af46d4739a97e6277ac550417f816e7372ce7d5736  crates/terrabase-server/src/native_backup_runtime.rs
104c59803aef7951b04f87f8166d07196780167cfe8ab3a5b78733247cd0ea4c  src/backup/worker.rs
```

Formatting check: `cargo fmt --all --check` passed. The production runtime was
not changed; only the test fixture's stream contract was made explicit.
