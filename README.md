# `terrascale` — Rust SDK for TerraScale

Document CRUD, provider-independent file storage, the typed schema builder (`#[derive(Schema)]` in
`terrascale-derive`), the typed SQL builder and raw parameterized SQL,
`subscribe`/`execute`, checkpoints and typed resets, Atlas discovery routing,
tenants, and management.

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

## Routing (Atlas discovery, ADR 0050)

A failover-affinity client (the default) targeting the public origin
`https://api.terrascale.tech` resolves member traffic through **Atlas
discovery**.

- **First use of a database** (`/api/v1/databases/{name}/…`) sends one
  unauthenticated metadata GET to
  `https://atlas.terrascale.tech/api/v1/discovery?database=…&branch=main`.
  The response is strictly validated (region codes, endpoint URLs on the
  `https://{region}.api.terrascale.tech` family — loopback dev origins are
  admitted for loopback clients — capability subsets, priority bounds) and
  cached per routing scope (origin, database, branch, project, tenant) for
  the document's `refreshSeconds` clamped to 60–3600 s. Concurrent discovery
  for one scope is deduplicated to a single request. Requests pick the first
  endpoint whose capabilities cover the operation (`write` for mutations) and
  stay pinned to it across refreshes while the region remains eligible.
  Round trips of successful regional requests feed a per-region EWMA (60 s
  halflife) sent as `X-TerraScale-Region-Rtt` on later discovery fetches, so
  Atlas ranks endpoints by measured latency — the header never goes to a
  regional member. Refresh fetches also carry `rev=<revision>` of the cached
  routing document whenever the scope holds one.
- **Discovery failure degrades to the region catalog**
  (`GET https://atlas.terrascale.tech/api/v1/regions`, cached five minutes);
  the request then goes directly to the first available regional host. If
  discovery and the catalog both fail, member routes fail with a
  typed `routing_unavailable` error.
- **Product routes** (`/api/v1/organizations*`, `/api/v1/projects*`,
  `/api/v1/regions/{code}`) target the configured API base, which the
  dashboard now serves directly. `/api/v1/regions` exactly is the catalog
  itself on Atlas. Database-less member routes (database create/list,
  tenants, operations) use catalog-selected regional endpoints, as do
  reactive attach and the reactive WebSocket when discovery cannot answer.
- **Failover and repair**: on a transport error or 52x/503 against a
  discovered regional endpoint, safe or idempotency-keyed requests move to
  the next endpoint in the document (the failed region is demoted per scope).
  A `421` adopts the member's `X-TerraScale-Placement` into the routing
  cache and marks the scope for one corrective `stale=1` rediscovery; `404`
  never triggers rediscovery. Idempotency-key retry gating and the strict
  URL allowlist still apply.
- **Affinity options**: `RegionAffinity::Failover` (default) uses
  discovery + catalog as above; `RegionAffinity::Strict` keeps the direct
  pinned regional base (`{region}.api.terrascale.tech`) with no discovery and
  requires `preferred_region`. Per-request region hints stay advisory `X-TerraScale-Preferred-Region`
  headers under discovery and never steer the base URL.
- **Configuration knobs**: `ClientConfig::builder().api_base_url(...)`
  selects the product origin (default `https://api.terrascale.tech`);
  `.atlas_base_url(...)` overrides the discovery origin (default
  `https://atlas.terrascale.tech`) and forces discovery on for custom API
  origins under failover affinity — used by tests and local development.
  Without that override, custom API origins make direct requests with no
  discovery. `.sync_base_url(...)` overrides the reactive delivery origin
  (default `https://sync.api.terrascale.tech`). Discovery requests are
  bounded at 3 s.

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

## ts-auth (hosted IdP) client

`client.auth_idp(slug)` returns the management client for one ts-auth
instance. ts-auth is a separate service from TerraBase: it has its own origin
(`auth_base_url`) and its own instance key (`auth_api_key`, the `sk_...`
management key), both optional on the config and independent of the TerraBase
credentials. With `https://auth.terrascale.tech`, it discovers the nearest
healthy regional auth origin from Atlas and calls that origin directly; safe
reads fail over and mutations are sent once. Set `.auth_reverse_proxy(true)`
to keep calls on the public proxy. Custom auth origins stay fixed. Calls fail
with a `Validation` error when `auth_base_url` is not
configured; management routes additionally require `auth_api_key`.

```rust,no_run
# async fn example() -> terrascale::Result<()> {
use terrascale::{ClientConfig, TerraScaleClient};

let client = TerraScaleClient::new(
    ClientConfig::builder()
        .api_key("ts_test_key")
        .auth_base_url("https://auth.terrascale.tech")
        .auth_api_key("sk_live_key")
        .build()?,
);
let auth = client.auth_idp("acme")?;
let page = auth.users().list(Some(20), None).await?;
let created = auth
    .users()
    .patch(&page.data[0].id, terrascale::auth_idp::AuthIdpUserPatch::new().with_name("Ada"))
    .await?;
let _ = created;
# Ok(())
# }
```

Families mirror the management client grouping: `users()`, `sessions()`,
`api_keys()`, `organizations()`, `connections()`, `directories()`,
`jwt_templates()`, `webhooks()`, `audit_events()`, `billing()`, `instance()`,
`oauth_clients()`, and the public OIDC routes under `oidc()` (discovery, JWKS,
token, revoke, introspect, userinfo — no instance key; client credentials go
in the token form body). One-time secrets (API keys, client secrets, SCIM
directory tokens, webhook signing secrets, impersonation tokens) are returned
only by the creating or rotating call and are redacted from `Debug`.
Webhook deliveries are verified with `verify_auth_idp_webhook` (or
`verify_auth_idp_webhook_with_tolerance` for a replay window), which checks
`sha256=` HMAC-SHA256 over `timestamp + "." + raw body` in constant time.
Reads are retried on transient failures like the rest of the SDK; mutations
are never retried (ts-auth has no idempotency contract). These routes are not
part of the TerraBase producer contract.

## Region catalog and member health

The public `GET https://atlas.terrascale.tech/api/v1/regions` endpoint is the
Atlas-published region catalog (`RegionCatalogResponse`) used by
`client.management().regions().catalog()` and, internally, as the
catalog-selection fallback for member routes. It includes the ordered region
list and regional hosts.

The same path on a regional TerraBase member is intentionally a different,
private-origin probe: it returns `HealthResponse` for that member. Use
`client.management().regions().health(region)` or `.status(region)`, which
target the member's `/api/v1/health` endpoint. A member response must never
be decoded as the Atlas catalog, and an Atlas catalog must never be used as a
member liveness result. This split is defined by TerraBase ADR 0043/0050 and
is also reflected in the generated member contract and the edge route
manifest.

## Layout

- `src/` — `terrascale` crate. Key modules are `config`, `discovery`,
  `regions`, `transport`, `client`, `documents`, `files`, `schema`, `sql`,
  `reactive`, `tenants`, `management`, and `auth_idp` (ts-auth hosted IdP
  client).
- `terrascale-derive/` — `#[derive(Schema)]` collection and field metadata.
- `tests/conformance.rs` — SDK behavior checks against a mock transport.
- `tests/producer_http_conformance.rs` — public SDK calls exercised against
  the pinned producer HTTP corpus; these do not replace live API qualification.
- `CONTRACT_VERSION` — pinned contract version; contract verification is run by
  `dotnet run scripts/contracts.cs` with an explicit source or immutable archive.

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

`cargo fmt --all --check`, `cargo clippy --locked
--workspace --all-targets --all-features -- -D warnings`, and `cargo
test --locked --workspace --all-features` run the Rust verification set. Contract
verification uses `dotnet run scripts/contracts.cs` with an explicit source or
immutable archive and remains fail-closed without one.

Cancellation tokens interrupt local waiting, including an in-flight request or
retry delay. They do not prove that a submitted mutation was rolled back; retain
its operation or idempotency identity and reconcile durable server state before
resubmitting. Operation discovery reports the producer’s explicit `covered_kinds`
and cancellation capability; it does not imply that every asynchronous owner
is represented or remotely cancellable.

## Supported versions

- Toolchain: Rust `1.98.1`, pinned by `rust-toolchain.toml` and used for all
  formatting, lint (`clippy -D warnings`), and locked workspace tests.
- Edition: 2021, declared in `Cargo.toml`. `cargo-audit` 0.22.2 runs the
  dependency advisory gate over the committed lockfile.
- Contract surface: the TerraBase API contract is pinned in `contracts/v1`
  (`CONTRACT_VERSION`, `CONTRACT_ARCHIVE_SHA256`) and regenerated with
  `src/generated_contract.rs` by `scripts/contracts.cs` from an explicit source
  or SHA-256-verified archive; tests fail when the committed bundle drifts from
  the generated digests.
