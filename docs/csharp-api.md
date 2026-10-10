# C# TerraBase client

The C# document contract is qualified against TerraBase commit
`263598b47ffc79398db394550e3c074acf4fc57d`. Its Documents API v1 uses
`/v1/projects/{project}/databases/{database}` with the existing Rust document,
patch, query, transaction, version and exact-number codecs.

```rust,no_run
use terrascale::v1::{Client, WriteOptions};
# async fn example(project: uuid::Uuid, access_token: &str) -> terrascale::v1::Result<()> {
let client = Client::for_project(project, access_token).build()?;
let database = client.database("orders");
database.create_collection("items").await?;
let items = database.collection("items");
let id = uuid::Uuid::new_v4();
let written = items.put(id, &serde_json::json!({"quantity": 1}),
    WriteOptions::new().if_none_match_any()).await?;
let unchanged = items.get_if_changed::<serde_json::Value>(id, &written.version).await?;
assert!(unchanged.is_none());
# Ok(())
# }
```

Pass a project UUID and an already exchanged ts-auth access token. Obtain
identity and credentials from the authentication SDK: TerraBase does not own
users, organizations, projects, billing or dashboard sign-in. The pinned
producer still authenticates data requests with legacy `tb_` keys. The real
binary test uses that revision's loopback bootstrap API and legacy data
credential; this is not evidence that its successor JWT-only API has shipped.

Normal first-party routing automatically discovers a verified eligible region;
no serving-region activation or manual region pin is a setup step. Existing
static endpoint/region-pin paths are confined to reviewed isolated tests and
producer qualification and remain inventoried for implementation removal.
Customer project scope and bearer authorization remain mandatory. HTTPS is
required; any loopback HTTP exception belongs only to isolated tests.
Redirects are never followed and project traffic bypasses environment proxies.

Default routing consumes Atlas's merged discovery v1 contract, introduced in
`ts-atlas` commit `59ee7cc`: anonymous `/v1/resolve?project=…&database=…` and
`/v1/keys`. ES256 verification binds issuer, project, database, region, API
origin and expiry. A session lasts at most 600 seconds; caching checks wall
and monotonic time. Expiry, `421` and redirects trigger `fresh=1`. A routing
correction permits one immediate retry of a read or read-only query, even
with general retries disabled. Writes return their original failure without
replay. Successful pinned responses must report the expected
`X-TerraBase-Region`; a mismatched mutation acknowledgement is uncertain.
Discovery never receives the TerraBase bearer token. For local resolver
tests, `.atlas_base_url(loopback_origin)` overrides Atlas.

Collection operations are `Database::create_collection`, `list_collections`,
`Collection::get_collection`, and synchronous `drop_collection`. Document
`list` takes a limit and opaque cursor. `Database::create` uses the pinned
producer's idempotent `PUT`; the future TS-589 management `POST` is not
qualified here. Database provisioning belongs to TerraBase; project creation
belongs to ts-auth. The legacy `/v1/dashboard` control client is unavailable
on project clients.

C# TerraBase rejects `Idempotency-Key`; project clients reject keyed writes
and transactions before dispatch. Transport/server failures and malformed
successful write acknowledgements retain `Error::is_uncertain()`. Reconcile
an uncertain mutation through reads and preconditions. Unsupported server
features (unique constraints, TTL, query `lower`/`explain`) remain server
errors; the SDK does not emulate them or widen their guarantees.

Run `nice -n 19 python3 scripts/terrabase/run.py` for the real C# binary suite.
The runner checks the pinned release hash, creates temporary TLS and storage,
provisions only through HTTP, requires the integration test to execute, and
cleans up its process and storage. Ordinary `cargo test` marks this test
ignored; CI explicitly runs the runner as a required step. Fast codec and
Atlas signature tests also run under `cargo test --locked --all-features`.
