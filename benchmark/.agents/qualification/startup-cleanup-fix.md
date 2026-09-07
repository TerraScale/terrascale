# Startup cleanup regression

Source at verification: HEAD `c7978feb46ab79f5039cf0001b41ba97d7b3ad7d` plus working changes in the two files below. Concurrent coordinator commits incorporated the startup stage extraction during this task; the fixer did not commit or push.

- `crates/terrabase-server/src/runtime.rs`: SHA256 `3babe1d603c1fecf0681409ae43e67d8aec543b7c4350fb5b291a721d3b155ca`
- `crates/terrabase-server/src/replication_peers/admission_tests.rs`: SHA256 `460824f044404ebd6f88a969e51dd74e7fe0806628fa7e2351d3fd1f2158b411`

The logged E0382 used a bare native supervisor JoinHandle both in normal shutdown and failure cleanup. Current source retains Option ownership and takes each handle once. The extracted `serve_admitted_production` is called by `run_production` and owns the actual post-recovery startup and cleanup path. A separate replication fixture compiler blocker was fixed by cloning placement into AcceptedMembership because the follower binding later borrows the same placement.

The new Linux regression constructs real Member/catalog owners, an authenticated QUIC pair, a PlacementJournal, ProductionAdmissionRuntime with retained Unix key delivery, and a nonempty NativeBackupRuntime with controlled in-memory ObjectDestination transport. Configured public/control settings reference a missing receipt-signing file. This fails after recovery worker/supervisor construction and invokes the production cleanup helper. Assertions cover the worker lock marker, release of task/executor native references, drained fleet/retained hosts, and reopening the exact recovery ledger.

First attempt: compile passed, fixture failed PlacementJournal::open with Storage because the fixture root was mode0755. Corrected the fixture to the required owner-private0700. First evidence retained in `startup-cleanup-fix.log`. Also replaced the initial unreachable publicNone injection with the reachable receipt-file failure before final verification.

Final command:

```
CARGO_BUILD_JOBS=2 nice -n 19 cargo test --locked -p terrabase-server post_worker_startup_failure_joins_owners_and_releases_ledger -- --nocapture
```

Result: exit0; actual named regression 1 passed, 0 failed, 507 filtered, 0.74s. Other test binaries selected zero tests. Exact log: `startup-cleanup-reachable.log`. Rustfmt and scoped git diff --check passed. Existing compiler warnings remain; no broad gate or live provider qualification claimed. `native_backup_runtime.rs` was not edited.

Prevention: concrete fixtures were available through existing test_keys, production transport and admitted registry constructors. Missing fixture assembly was implementation work, not an external credential dependency. Fixture paths must satisfy the real journals' owner-private permission contract.
