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
`f35fee58f1097a202c68354dd07218a05a0f9808f15fb8a947f34513280bc71d`

Added focused test `pool_unit_boundaries_preserve_signed_budget_contract`.
It verifies `i64::MAX` remains representable, values above it fail closed,
zero remains accepted by the existing allocation-unit guard, and negative
allocation values are rejected before any pool work/reservation path.

Public regression diagnosis/fix (directly scoped usage tests):
`canonical_signed_delivery_is_accepted_over_http_and_persisted` now expects
the canonical persisted refill operation ID `refill-vector-1`. In
`public_writes_consume_signed_included_and_paid_allocations_once`, the expired
payload is updated and resolved using its actual persisted refill identity
(`refill-{operation}`); the prior test updated a nonexistent API operation
identity and therefore left the valid grant in place. Expiry verification is
unchanged and still asserts `AllocationError::Expired`.

Test source SHA-256:
`42ffbcc127ce6f552de291806c7d8e13b69b8ecef77b03967705c27a8037d5ec`

Public regression follow-through: the allocation-table aggregate now queries
the persisted refill identity (`refill-{operation}`), matching admission and
pool lookup ownership. The prior API operation key returned no rows, making
`SUM` legitimately `NULL`; this was a test identity bug, not an empty-budget
scenario. The query still requires one row and exact `(1, 1, 0)` totals, so
missing writes or allocations remain failures. Exact selectors:
`api::tests::budget_http_tests::canonical_signed_delivery_is_accepted_over_http_and_persisted`
and
`api::tests::budget_http_tests::public_writes_consume_signed_included_and_paid_allocations_once`.
