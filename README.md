# `terrascale` — Rust SDK for TerraScale

Same core surface as `@terrascale/sdk` (GOAL D-13): document CRUD, the typed
schema builder (`#[derive(Schema)]` stub in `terrascale-derive`), the typed
SQL builder + raw parameterized SQL, `subscribe`/`execute`, checkpoints and
typed resets, region pinning, tenants, and management.

```rust,no_run
use terrascale::{ClientConfig, RegionCode, TerraScaleClient};

# async fn example() -> terrascale::Result<()> {
let client = TerraScaleClient::new(
    ClientConfig::builder()
        .api_key("ts_test_key")
        .preferred_region(RegionCode::Gru)
        .build()?,
);
let docs = client.documents();
# Ok(())
# }
```

## Layout

- `src/` — `terrascale` crate: `config`, `regions`, `transport`, `client`,
  `documents`, `schema`, `sql`, `reactive`, `tenants`, `management`.
- `terrascale-derive/` — `#[derive(Schema)]` stub (collection-name mapping;
  field mapping arrives with codegen).
- `tests/conformance.rs` — conformance suite stub against a mock transport;
  runs the pinned request shapes until terrabase publishes HTTP + reactive
  fixtures.
- `CONTRACT_VERSION` — pinned contract version (`make contracts`).

## Verify

`make check` (= `cargo fmt --check` + `cargo clippy --all-targets -- -D
warnings` + `cargo test`).
