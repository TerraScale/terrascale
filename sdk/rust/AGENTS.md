# ts-rust

## Layout and checks

Run area commands from `sdk/rust` in the monorepo.

- One `terrascale` crate: client/config/transport in `src/`, project-scoped API in `src/v1/`, identity in `src/auth_idp.rs`.
- Use the toolchain pinned in `rust-toolchain.toml` and preserve `Cargo.lock`.
- Build: `cargo build --locked --all-features`. From the repository root, `make sdk-rust-check` builds every target.
- Formatting/lint: `cargo fmt --all -- --check` and `cargo clippy --locked --all-targets --all-features -- -D warnings`.
- PostgreSQL support is behind the existing `postgres` Cargo feature. Do not describe PostgreSQL or security single-writer admission as qualified without evidence.

## Configuration

- Keep features always on; no feature flags, enable switches, opt-ins or kill switches. Keep one correct value in code, with no new tuning knobs.
- No fakes, demo stores or skip-auth. The journey in `e2e/` is the only test; an SDK operation gets a step in its runners (`e2e/AGENTS.md`).
- Keep the switch-style configuration check; unavoidable third-party names need a narrow allowance and a one-line reason.
- README.md records implementation and qualification gaps. Do not claim unshipped behavior or bypass authorization, tenant scope, registered callbacks, payment consent or safety guards.
