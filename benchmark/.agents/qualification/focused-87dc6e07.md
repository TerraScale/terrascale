# Focused Rust verification

Revision at start: `87dc6e07841fd63975c448bcf56593cab4807fb`.

Command: one sequential shell lane using `nice -n 19 cargo test --locked` for core `storage_closure`, core `shard_split`, server `credential_rotation`, `production_lifecycle_control`, `origin_proof`, and `native_recovery`; output is in [focused-87dc6e07.log](focused-87dc6e07.log).

Results:

- Core storage closure: **PASS**, 4 passed.
- Core shard split: **PASS**, 23 passed.
- Server credential rotation: **FAIL**, 2 passed, 1 failed. `durable_rotation_changes_actual_verification_and_rejects_replays` failed at `crates/terrabase-server/src/credential_rotation.rs:767`, `assertion failed: accepts(&bound.origin, old)`.
- Server production lifecycle control: **PASS**, 3 passed.
- Server origin proof selector: **PASS**, 1 passed.
- Server native recovery: **FAIL**, 44 passed, 1 failed. `original_restore_rpc_reaches_readback_without_source_and_rejects_changed_request` failed at `crates/terrabase-server/src/native_recovery.rs:1363`: `original RPC reaches durable readback without source: "native restore admission refused"`.

The worktree gained a concurrent `crates/terrabase-server/src/runtime.rs` edit during the lane; no source edits were made here. Current hashes are recorded in the handoff message and should be refreshed before reruns.
