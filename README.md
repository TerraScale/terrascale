# `terrascale` — Rust SDK for TerraScale

Document CRUD, the typed schema builder (`#[derive(Schema)]` in
`terrascale-derive`), the typed SQL builder and raw parameterized SQL,
`subscribe`/`execute`, checkpoints and typed resets, region pinning, tenants,
and management.

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

- `src/` — `terrascale` crate. Key modules are `config`, `regions`,
  `transport`, `client`, `documents`, `schema`, `sql`, `reactive`, `tenants`,
  and `management`.
- `terrascale-derive/` — `#[derive(Schema)]` collection and field metadata.
- `tests/conformance.rs` — SDK behavior checks against a mock transport.
- `tests/producer_http_conformance.rs` — public SDK calls exercised against
  the pinned producer HTTP corpus; these do not replace live API qualification.
- `CONTRACT_VERSION` — pinned contract version (`make contracts`).

The derive accepts named-field and unit structs and implements
`terrascale::schema::Document`. A type name supplies the default collection
name; use `#[schema(collection = "...")]` or `#[schema(name = "...")]` to
override it. `Option<T>` fields are optional, and supported scalar types are
inferred as string, integer, float, boolean, bytes (`Vec<u8>`), timestamp, or
JSON. Other application types fall back to JSON and can use
`#[schema(type = "...")]`.

Field metadata supports `#[schema(indexed)]`, `#[schema(unique)]`,
`#[schema(optional)]`, `#[schema(rename = "...")]`, and
`#[schema(merge = "...")]`; `#[schema(skip)]` and `#[serde(skip)]` omit a
field. Container metadata supports `ttl`, history retention, and a collection
merge policy. Serde `rename_all` rules are applied when an explicit field
rename is absent.

## Verify

`nice -n 19 make check` runs `make contracts`, `cargo fmt --all --check`,
Clippy with warnings denied, and the test suite. `make contracts` validates the
producer bundle and regenerates `contracts/<CONTRACT_VERSION>/` plus
`src/generated_contract.rs`; it requires an immutable `CONTRACT_URL` together
with `CONTRACT_ARCHIVE_SHA256`, or an explicit `CONTRACT_SOURCE` directory for
local producer development. Missing provenance fails the check.

Cancellation tokens interrupt local waiting, including an in-flight request or
retry delay. They do not prove that a submitted mutation was rolled back; retain
its operation or idempotency identity and reconcile durable server state before
resubmitting. Operation discovery reports the producer’s explicit `covered_kinds`
and cancellation capability; it does not imply that every asynchronous owner
is represented or remotely cancellable.
