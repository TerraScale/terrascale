# API operations mechanics handoff

Source packet HEAD: `7f576d8a8af3967e639ed7325a06c39c15ef87a2`.
Updated source SHA-256:
`bd741734bade63da3d55af2d0778d630cd667c49dd2812b6988424cd686ca6e7`

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

