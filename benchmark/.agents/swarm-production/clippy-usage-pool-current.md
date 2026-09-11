# Usage-pool clippy slice handoff

Updated only `crates/terrabase-server/src/usage_pool.rs` for the approved
safe mechanics:

- Replaced the wildcard parent import with explicit names.
- Added `PoolEngineResolver` without changing closure bounds or ownership.
- Used `cast_unsigned` for verified-positive allocation units and a checked
  `u64` to `i64` conversion for requested units before signed budget reads;
  unrepresentable values fail closed.
- Replaced option `map(...).unwrap_or_else(...)` pairs with `map_or_else`.
- Changed `acknowledge_pool` to borrow `Ack`; its sole caller now borrows the
  owned request inside the blocking closure. Ledger transaction ordering is
  unchanged.
- Left the cohesive `too_many_lines` diagnostic untouched.

Validation passed:

- `rustfmt --check crates/terrabase-server/src/usage_pool.rs`
- `git diff --check`

No Cargo commands were run; `project_capacity_acceptance_fixer` owns the
consolidated build and focused selectors.

Source SHA-256:
`445eccea49fd83bb581d144d54d21fb222ab70ef49bd47a68b313b6591696e8c`

