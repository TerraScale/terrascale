# `terrascale` — Rust SDK for TerraScale

Document CRUD, the typed
schema builder (`#[derive(Schema)]` in `terrascale-derive`), the typed
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

With failover affinity, a preferred Region is sent on global first contact.
When a successful response identifies its serving Region, subsequent REST
requests pin there. Strict affinity routes directly to the configured Region
and preserves it when another Region is advertised.

## Layout

- `src/` — `terrascale` crate: `config`, `regions`, `transport`, `client`,
  `documents`, `schema`, `sql`, `reactive`, `tenants`, `management`.
- `terrascale-derive/` — `#[derive(Schema)]` collection and field metadata.
- `tests/conformance.rs` — SDK behavior checks against a mock transport.
- `tests/producer_http_conformance.rs` — public SDK calls exercised against
  the pinned producer HTTP corpus; these do not replace live API qualification.
- `CONTRACT_VERSION` — pinned contract version (`make contracts`).

## Verify

`nice -n 19 make check` validates and generates contracts, checks formatting,
runs Clippy with warnings denied, and runs tests. Supply an immutable
`CONTRACT_URL` with `CONTRACT_ARCHIVE_SHA256`, or an explicit `CONTRACT_SOURCE`
for local producer development. Missing provenance fails the check.

Cancellation tokens interrupt local waiting, including an in-flight request or
retry delay. They do not prove that a submitted mutation was rolled back; retain
its operation or idempotency identity and reconcile durable server state before
resubmitting. Operation discovery reports the producer’s explicit `covered_kinds`
and cancellation capability; it does not imply that every asynchronous owner
is represented or remotely cancellable.
