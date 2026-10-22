# TerraScale Rust SDK

`terrascale::v1` implements TerraBase's public project and platform HTTP APIs.
Start with the [current API guide](docs/current-api.md). Entity IDs use canonical
uppercase ULIDs; database/collection selectors use names; document keys are raw strings.

```rust,no_run
use terrascale::v1::Client;
# async fn example(project: &str, access_token: &str) -> terrascale::v1::Result<()> {
let client = Client::for_project(project, access_token).build()?;
let document = client.database("orders").collection("items")
    .get::<serde_json::Value>("customer/a+b").await?;
# let _ = document;
# Ok(())
# }
```

Project requests use a ts-auth access token. Anonymous Atlas resolution verifies
the signed project/database/region/API-origin session before sending that token
to TerraBase. Static endpoint overrides retain TLS, bearer isolation and region
pin validation. Mutations are not automatically replayed after uncertain outcomes;
read their effects before deciding to retry. The SDK exposes no idempotency-key API.

The separate `TerraScaleClient::auth_idp()` client retains ts-auth support and
credential isolation. Optional PostgreSQL support remains available through the
`postgres` Cargo feature and `examples/postgres_journey.rs`.

Collection [shape reads](docs/shape-reads.md) support snapshot pages, sealed
chunks and live polling. Database changes use a raw SSE byte stream; callers
own parsing, deduplication, cursor persistence and reconnect decisions.

Validation uses HTTP fakes and contract fixtures only. SDK tests, CI and helpers
never download or run a TerraBase binary. GitHub CI runs formatting, all-feature
tests and Clippy; source coverage does not establish live deployment health.
