# ts-rust

Read the root [AGENTS.md](../../AGENTS.md).

## Layout and checks

Run area commands from `sdk/rust/` in the monorepo.

- One `terrascale` crate: client/config/transport in `src/`, project-scoped API in `src/v1/`, identity in `src/auth_idp.rs`.
- Use the toolchain pinned in `rust-toolchain.toml` and preserve `Cargo.lock`.
- Build: `cargo build --locked --all-features`. From the repository root: `make sdk-rust-check`.
- Formatting/lint: `cargo fmt --all -- --check` and `cargo clippy --locked --all-targets --all-features -- -D warnings`.
- PostgreSQL support is behind the existing `postgres` Cargo feature. Do not describe PostgreSQL or security single-writer admission as qualified without evidence.
- `README.md` records implementation and qualification gaps; do not claim unshipped behavior.
