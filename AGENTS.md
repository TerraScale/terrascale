# ts-rust

Follow [workspace Shipping code](/home/mariogk/Projects/TerraScale/AGENTS.md#shipping-code-user-2026-10-29-go-fast-break-things-fast-q-trunk-q-local-check).
## Layout and checks

- One `terrascale` crate: client/config/transport in `src/`, project-scoped API in `src/v1/`, identity in `src/auth_idp.rs`, integration tests in `tests/`.
- Use the toolchain pinned in `rust-toolchain.toml` and preserve `Cargo.lock`.
- Fast: `cargo test --locked`.
- Full tests: `cargo test --locked --all-features`; build: `cargo build --locked --all-features`.
- Formatting/lint: `cargo fmt --all -- --check` and `cargo clippy --locked --all-targets --all-features -- -D warnings`.
- PostgreSQL support is behind the existing `postgres` Cargo feature. Do not describe PostgreSQL or security single-writer admission as qualified without evidence.

## Configuration

- Keep features always on; no feature flags, enable switches, opt-ins or kill switches. Keep one correct value in code, with no new tuning knobs.
- Fakes, demo stores and skip-auth belong only in test code and test binaries.
- Keep the switch-style configuration check; unavoidable third-party names need a narrow allowance and a one-line reason.
- README.md records implementation and qualification gaps. Do not claim unshipped behavior or bypass authorization, tenant scope, registered callbacks, payment consent or safety guards.
