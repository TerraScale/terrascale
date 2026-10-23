# C# TerraBase client

The source compatibility target is TerraScale/terrascale
`e30ffdc287530551b161b0130220be53d3c0475e`. See the
[compatibility matrix and evidence](compatibility-20261013.md) for qualification limits.

```rust,no_run
use terrascale::v1::{Client, WriteOptions};
# async fn example(project: &str, access_token: &str) -> terrascale::v1::Result<()> {
// project is a canonical uppercase ULID from ts-auth.
let client = Client::for_project(project, access_token).build()?;
let database = client.database("orders");
database.create().await?; // POST, requires databases:write
database.create_collection("items").await?;
let items = database.collection("items");
let key = "customer/a+b";
let written = items.put(key, &serde_json::json!({"quantity": 1}),
    WriteOptions::new().if_none_match_any()).await?;
assert!(items.get_if_changed::<serde_json::Value>(key, &written.version).await?.is_none());
let result = database.run_sql::<serde_json::Value>("SELECT * FROM items LIMIT 10").await?;
# let _ = result;
# Ok(())
# }
```

Project IDs must be canonical 26-character uppercase ULIDs. Point document operations use
`GET/PUT/PATCH/DELETE /v1/projects/{project}/databases/{name}/collections/{name}/documents/by-key?id=…`.
Document IDs are `DocumentId` (`ulid::Ulid`), sent as canonical uppercase ULIDs; response IDs in
any other form are rejected. `ulid_for(collection, key)` derives a stable ID from a natural key
before the first request. Generated creates return ULIDs.
`Tx::create` accepts `Option<DocumentId>` and `OpResult::id()` returns `Option<DocumentId>`.

The bearer credential is an already exchanged ts-auth project access token. The SDK does not
mint credentials or grant scopes. The server validates issuer, exact audience, signed token
type, lifetime, canonical subject, project and database restrictions, and exact operation scopes.
Wrong project/database is 404; invalid credentials are 401; missing scope is 403.
Database creation uses `POST /v1/projects/{project}/databases` with `{"name":…}` and
`databases:write`. Collection/document reads need `documents:read`; their writes and
transactions need `documents:write`; SQL needs `databases:read`.

The audited implementation still supports `If-Match`, `If-None-Match` and transaction
preconditions. These are retained because the current server implements them. It rejects
`Idempotency-Key`; the SDK rejects keyed project mutations before dispatch. A caller may
explicitly repeat a PUT with the same key and bytes, but it is a new commit, not a deduplicated
receipt; concurrent intervening changes can be overwritten. Mutations do not automatically
replay after transport or 503 failures. The existing `429 rate_limited` retry requires that
exact code; no permission is inferred from status alone.

Project errors preserve actual HTTP status, exact response bytes and any `X-Request-ID` in
`Error::Response`. Use `status()`, `code()`, `op()`, `is_not_found()`, `is_uncertain()`,
`raw_body()` and `request_id()`, or inspect the boxed typed `error`/standard error source.
Malformed successes and mismatched acknowledgements retain diagnostics; failed mutation
acknowledgements are uncertain. Oversized bodies retain only the bounded prefix.
The server currently emits `{"error":{…}}`. RFC 9457 decoding is defensive test coverage;
server problem responses and request-ID generation remain unqualified dependencies.

Lists and queries preserve opaque cursors; empty intermediate pages do not mean completion.
The page iterator stops at a null cursor and rejects repeated or missing/empty cursors.
HTTP SQL returns bounded rows and a truncation flag; it has no parameters or cursors.
Use `Box<RawValue>` to preserve exact document numbers, including SQL values.

Project clients send every request to `https://api.terrascale.tech` (`DEFAULT_BASE_URL`); the
public edge routes it to the owning region. There is no client-side resolve step. Static
endpoint/region overrides are limited to isolated tests and producer qualification.

The implemented `/changes` SSE route has no Rust SDK consumer. Native subscriptions and the
optional PostgreSQL consumer target older protocols. The existing real-binary runner remains
pinned to `263598b47ffc79398db394550e3c074acf4fc57d` and cannot qualify this current contract;
it needs a current release artifact and an authorized JWT fixture. No test is skipped or weakened
to conceal that gap.
