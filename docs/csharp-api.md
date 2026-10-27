# C# TerraBase client

Use the [current API guide](current-api.md) for the full API surface. This walkthrough uses
an existing database and a new collection that it removes when finished.

```rust,no_run
use terrascale::v1::{ulid_for, Client, WriteOptions};
# pub async fn example(project: &str, api_key: &str, database_name: &str, collection_name: &str) -> terrascale::v1::Result<()> {
// project is a canonical uppercase ULID from ts-auth.
let client = Client::for_project_api_key(project, api_key).build()?;
let database = client.database(database_name);
database.create_collection(collection_name).await?;
let items = database.collection(collection_name);
let id = ulid_for(collection_name, "customer/a+b");
let written = items.put(id, &serde_json::json!({"quantity": 1}),
    WriteOptions::new().if_none_match_any()).await?;
assert!(items.get_if_changed::<serde_json::Value>(id, &written.version).await?.is_none());
let statement = format!("SELECT * FROM \"{}\" LIMIT 10", collection_name.replace('"', "\"\""));
let result = database.run_sql::<serde_json::Value>(&statement).await?;
assert_eq!(result.matched, 1);
items.delete(id, WriteOptions::new().if_match(written.version)).await?;
items.drop_collection().await?;
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

The API-key client exchanges and refreshes a project access token through ts-auth.
For an already exchanged static bearer token, use `Client::for_project`; static tokens
are not refreshed. The SDK does not grant scopes. The server validates issuer, exact audience, signed token
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
replay after transport or 503 failures. The `429` mutation retry requires the exact code
`rate_limited` or `tenant_busy` (both refuse admission); no permission is inferred
from status alone.

Project errors preserve actual HTTP status, exact response bytes and any `X-Request-ID` in
`Error::Response`. Use `status()`, `code()`, `op()`, `is_not_found()`, `is_uncertain()`,
`raw_body()` and `request_id()`, or inspect the boxed typed `error`/standard error source.
Malformed successes and mismatched acknowledgements retain diagnostics; failed mutation
acknowledgements are uncertain. Oversized bodies retain only the bounded prefix.
The server currently emits `{"error":{…}}`; the SDK also accepts RFC 9457 errors.

Lists and queries preserve opaque cursors; empty intermediate pages do not mean completion.
The page iterator stops at a null cursor and rejects repeated or missing/empty cursors.
The document SELECT subset in `run_sql` returns bounded rows and a truncation flag; it
has no parameters or cursors. For parameter binding and typed relational rows, use
`Database::sql()` as shown in the [README](../README.md#relational-sql). These APIs query
separate document collections and relational tables, respectively.
Use `Box<RawValue>` to preserve exact document numbers, including SQL values.

Project clients send every request to `https://api.terrascale.tech` (`DEFAULT_BASE_URL`); the
public edge routes it to the owning region. There is no client-side resolve step. Static
endpoint/region overrides are limited to isolated tests and producer qualification.

Database changes are available through `Database::changes` (typed events) or
`Database::change_bytes` (raw SSE). Typed streams resume with the last cursor after a clean
end. API-key clients acquire a cached or refreshed token on reconnect; static-token clients
need a fresh client after token expiry. See [API-key authentication](api-key-auth.md). See [shape reads](shape-reads.md) for collection
snapshots and live polling.
