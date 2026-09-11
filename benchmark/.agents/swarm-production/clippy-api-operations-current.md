# API operations mechanics handoff

Source packet HEAD: `7f576d8a8af3967e639ed7325a06c39c15ef87a2`.
Updated source SHA-256:
`efaa3034d117f64669d94a19d284d0006b7ec41502bb20940759cb5ca53bdd37`

Applied only the approved Slice B mechanics in
`crates/terrabase-server/src/api_operations.rs`:

- Merged the identical recovery-state wildcard mapping.
- Collapsed the cursor validation conditional with a let-chain while retaining
  cursor precedence and fallback behavior.
- Changed unit error patterns to `Err(())` and used `let ... else` for the
  local operation page error path.
- Removed needless `member_id` borrows.
- Used `clone_from` for owned cursor identifiers only.

The operation visibility, ownership filtering, cursor IDs, and response/error
mapping are unchanged. Structural `too_many_lines` was left untouched.

Validation passed:

- `rustfmt --check crates/terrabase-server/src/api_operations.rs`
- `git diff --check`

Cargo compilation/tests were not run; `terrabase_clippy_batch` owns those
checks.

Frozen compile follow-up: restored `member_id(&state)` at the outer
`list_operations` handler (line 285), where `state` is `Arc<ApiState>`;
inner helper calls retain direct `&ApiState` borrows. Formatting and diff
checks remain clean.
