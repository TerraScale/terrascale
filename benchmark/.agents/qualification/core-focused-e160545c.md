# Core focused verification

- Revision: `e160545cc1aefc562aab91818f254592f0cc960c`
- Selectors: `storage_closure`, `shard_split`
- Test command: `set -o pipefail; { nice -n 19 cargo test -p terrabase --lib storage_closure --locked; nice -n 19 cargo test -p terrabase --lib shard_split --locked; } 2>&1 | tee .agents/qualification/core-focused-e160545c.log`
- Result: **FAIL** at compilation for both selectors. No tests executed.
- Exact compile groups: `DocumentValue` missing in `src/storage/participant.rs` test code (`E0433`, lines 98 and 308); `open_engine`, `tenant_scope`, and `operation` are private in `src/engine/tests.rs` but referenced by `src/engine/shard_split_tests.rs` (`E0603`).
- Logs: [core-focused-e160545c.log](core-focused-e160545c.log)

Core clippy command:

`nice -n 19 cargo clippy -p terrabase --lib --locked -- -D warnings`

Result: **FAIL**, 54 errors. Full output: [core-clippy-e160545c.log](core-clippy-e160545c.log). The dominant actionable group is dead cross-file and shard split code (unused constants/types/functions/methods); remaining groups include missing error docs, complexity/line-count/argument-count lints, and dropping a mutable reference.

Source hashes at run: `src/storage/sqlite/storage_closure.rs` `c32547e18a55d4db37bd90193f5b2dbec0bd6eb9102ca524336170cf54271d4b`; `src/engine/shard_split.rs` `63a8cdd1ca8afd8b0e5bf58d51519d3cbba082cc44e4f80f10ea82ef19b38527`; `src/usage/storage_closure.rs` `3f93b1f73d2e752327ef301cea6d7728400a913b91a33f9d5821bc9ec764856e`.
