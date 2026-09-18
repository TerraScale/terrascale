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

## Reactive transports

`subscribe_live` defaults to bounded HTTP long-polling of the immutable
view log. `SubscribeOptions::with_transport` opts into direct member
delivery instead: `Transport::WebSocket` (the `terrascale.sync.v1` socket
with its subscribe opening exchange) or `Transport::ServerSentEvents`
(`GET .../stream`, one `text/event-stream` connection delivering the same
MessagePack envelopes as base64 `data` events with `id:` resume offsets).
Both direct transports authenticate with the attach-issued capability
header; a dropped stream or terminal `error` event surfaces as a typed
`ResetReason::Reconnect` and the caller reattaches from its last
checkpoint. The database-scoped `GET /api/v1/databases/{name}/reactive`
WebSocket remains a declared contract gap: the pinned contract documents no
opening exchange for it.

`client.tenants().authorization_lease(tenant)` reads the published
`GET /api/v1/authorization/leases/{tenant}` lease, and
`client.management().schemas().rollback_migration(...)` rolls back an
in-progress schema migration with its required `Idempotency-Key` and
`If-Match` headers.

## Region discovery and member health

The public `GET https://api.terrascale.tech/api/v1/regions` endpoint is the
edge-owned region catalog (`RegionCatalogResponse`) used by
`client.management().regions().catalog()`. It is the only endpoint used for
initial discovery and includes the ordered region list and regional hosts.

The same path on a regional TerraBase member is intentionally a different,
private-origin probe: it returns `HealthResponse` for that member. Use
`client.management().regions().health(region)` or `.status(region)`, which
target the member's `/api/v1/health` endpoint. A member response must never
be decoded as the edge catalog, and an edge catalog must never be used as a
member liveness result. This split is defined by TerraBase ADR 0043 and is
also reflected in the generated member contract and the edge route manifest.

## Layout

- `src/` — `terrascale` crate. Key modules are `config`, `regions`,
  `transport`, `client`, `documents`, `schema`, `sql`, `reactive`, `tenants`,
  and `management`.
- `terrascale-derive/` — `#[derive(Schema)]` collection and field metadata.
- `tests/conformance.rs` — SDK behavior checks against a mock transport.
- `tests/producer_http_conformance.rs` — public SDK calls exercised against
  the pinned producer HTTP corpus; these do not replace live API qualification.
- `CONTRACT_VERSION` — pinned contract version; contract verification is run by
  `python3 scripts/contracts.py` with an explicit source or immutable archive.

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

`nice -n 19 cargo fmt --all --check`, `nice -n 19 cargo clippy --workspace
--all-targets --all-features -- -D warnings`, and `nice -n 19 cargo test
--workspace --all-features` run the Rust verification set. Contract
verification uses `python3 scripts/contracts.py` with an explicit source or
immutable archive and remains fail-closed without one.

Cancellation tokens interrupt local waiting, including an in-flight request or
retry delay. They do not prove that a submitted mutation was rolled back; retain
its operation or idempotency identity and reconcile durable server state before
resubmitting. Operation discovery reports the producer’s explicit `covered_kinds`
and cancellation capability; it does not imply that every asynchronous owner
is represented or remotely cancellable.
