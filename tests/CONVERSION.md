# Rust verification guide

The active service and tests are in the Cargo workspace. Previous .NET tests
remain in Git history for reference, not as Rust acceptance evidence.

## Current checks

Run `mise exec -- cargo fmt --all -- --check`, `mise exec -- cargo clippy
--workspace --all-targets --locked -- -D warnings`, and `mise exec -- cargo
test --workspace --all-targets --locked` with Rust 1.98.1 and the sibling
`../ts-rust` checkout at the revision pinned in CI. The tests in
`crates/auth-app/tests/` bind a real loopback TCP listener
and call the composed Axum router over HTTP. `http_surface` covers OpenAPI,
Scalar, management isolation, PKCE, SCIM token scope, Stripe checkout, and
fail-closed readiness. `hosted_identity_security` and
`organization_regressions` cover selected identity and organization failure
paths. Rust unit and `auth-store` tests cover crypto and persistence contracts.

A deterministic TerraBase HTTP fixture may stand in for the unfinished live
backend to check the exact requests sent through `ts-rust` and the
`auth-store` query adapter. Label these as contract fixture tests. A fixture
pass does not establish behavior against a deployed TerraBase database.

## Remaining acceptance work

Review each Rust route family and its storage contract for authorization
failures, persistence conflicts, and ambiguous commits. Browser-visible UI
and Datastar behavior need browser coverage. The coverage map in
`docs/parity.md` records which Rust tests exercise each family and which
integrations still require a live external system.

The optional `scripts/smoke-real-stack.sh` runs the Rust release binary against
a separately supplied TerraBase member. The backend implementation is not
complete yet, so this script is a future gate and is not CI acceptance evidence.
