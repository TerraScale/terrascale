# `terrascale::v1` API

The name-based TerraBase v1 client. Contract: terrabase `feat/v1-contracts`
at `7bc74f9d` (`docs/api-v1.md`, `query-v1.md`, `control-plane-v1.md`).
Reference implementation: ts-go `feat/v1-client` (`v1/`).

The public API skeleton was introduced in commit
`d26086340ea1c0a296e618e7898d8a1e4a2ea4ff` on branch `feat/v1-client`. Pin
that commit (or a later one on the branch) to build against it.

## Numbers

The crate does not enable serde_json's `arbitrary_precision`. Document
bodies are generic over `Serialize`/`DeserializeOwned`; the default body type
is `Box<serde_json::value::RawValue>`, which keeps the exact JSON text.
`Number` holds exact decimal text and serializes as a bare JSON number. Wire
`u64` quantities (offsets, sequences, timestamps, counts) are decimal strings
decoded into `u64` (`u64_string`, `opt_u64_string` serde adapters).
`RawValue` and `Number` do not survive `#[serde(flatten)]` or untagged enums.

## Client

```rust
let client = Client::builder(base_url, token)        // ClientBuilder
    .add_root_ca_pem(pem)                            // extra dev root CA, PEM
    .timeout(Some(Duration))                         // per attempt, default 30 s
    .connect_timeout(Some(Duration))                 // default 10 s
    .retry_policy(RetryPolicy { max_retries, base_delay, max_delay, max_retry_after })
    .max_response_bytes(usize)
    .build()?;                                       // -> Client; build_control() -> ControlClient
client.database("main") -> Database
client.control() -> ControlClient
```

TLS 1.3 only; HTTPS only except loopback HTTP; redirects are not followed.
Retry policy: `429` always (after `Retry-After`, capped); transport errors and
the transient 503s (`capacity`, `storage_unavailable`,
`dependencies_unavailable`, `commit_uncertain`) only for reads, queries and
requests with an `Idempotency-Key`; `500 internal_error` only with a key.

## Documents

```rust
let users: Collection = db.collection("users");
users.get::<T>(id: Uuid) -> Result<Document<T>>                     // 404 -> Error::NotFound { etag, .. }
users.get_if_changed::<T>(id, &Version) -> Result<Option<Document<T>>> // None = 304
users.put(id, &doc, WriteOptions) -> Result<WriteResult>            // created = 201
users.create(&doc, WriteOptions) -> Result<WriteResult>             // POST, server UUIDv7
users.patch(id, &Patch, WriteOptions) -> Result<WriteResult>
users.delete(id, WriteOptions) -> Result<WriteResult>

WriteOptions::new().if_match(Version).if_none_match_any().idempotency_key(IdempotencyKey)
enum Precondition { IfMatch(Version), IfNoneMatchAny }
struct WriteResult { id: Uuid, version: Version, offset: u64, durability: Durability, created: bool, location: Option<String> }
struct Document<T = Box<RawValue>> { id: Uuid, version: Version, state: DocumentState<T> }
enum DocumentState<T> { Value(T), Conflict(Vec<Sibling<T>>) }
struct Sibling<T> { event: Event, deleted: bool, document: Option<T> }
Patch::merge(&v)? | Patch::ops(vec![PatchOp::set(p,&v)?, PatchOp::unset(p), PatchOp::inc(p, n), PatchOp::append(p,&v)?])
```

## Transactions

```rust
let mut tx = Tx::new();
tx.create(coll, Option<Uuid>, &doc)
  .put(coll, id, &doc, Option<Precondition>)
  .patch(coll, id, Patch, Option<Version>)
  .delete(coll, id, Option<Version>)
  .assert(coll, id, Filter)
  .assert_absent(coll, id)
  .assert_count(coll, Filter, CountBounds { gte, lte })   // at_least/at_most/between
  .idempotency_key(IdempotencyKey);
db.commit(&tx) -> Result<TxResult { offset, durability, results: Vec<OpResult> }>
enum OpResult { Write { id, version }, Asserted { count: Option<u64> } }
```

Failures carry the op index: `err.op() -> Option<usize>`.

## Queries

```rust
Filter::{and, or, not, eq, ne, lt, lte, gt, gte, in_, nin, exists, prefix, contains}
Filter::from_json(r#"{"eq":["/email","a@b.c"]}"#)?
Path::from("/email") | Path::lower("/email") | Path::Id ("$id")
Query::new().filter(f).sort(SortKey::desc("/created_at")).project(["/email"]).limit(50).cursor(c).count(true).explain(Explain::Plan)
coll.query::<T>(&Query) -> Result<QueryPage<T> { items, next_cursor, count: Option<Count { value, exact }>, explain }>
coll.pages::<T>(Query) -> Pages<T>;  pages.next_page().await -> Result<Option<QueryPage<T>>>
coll.query_all::<T>(Query, max_items) -> Result<Vec<Document<T>>>
```

## IDs

```rust
uuidv5_for(collection, key) -> Uuid
Version::new(s)? ; IdempotencyKey::new(s)? | ::random() | ::derive(&[parts])
is_valid_name(name) -> bool
```

## Errors

```rust
enum Error {
    PreconditionFailed { op }, UniqueViolation { constraint, op }, NotFound { etag, op },
    ConflictUnresolved { op }, HomeRegionRequired, CommitUncertain, IdempotencyConflict,
    IdempotencyExpired, RateLimited { retry_after }, Forbidden { op }, InvalidKey,
    InternalError, Api { status, code, message, op, details }, Transport(String),
    InvalidInput(String), Decode(String),
}
err.status() / err.code() / err.op() / err.is_not_found() / err.is_uncertain()
```

## Control plane (`/v1/dashboard`)

Every mutation takes `&IdempotencyKey` first.

```rust
ctl.create_tenant(&key, &CreateTenant { name, external_ref, limits, advisor_external }) -> Tenant
ctl.list_tenants(Option<&str /* external_ref */>, &ListOptions) -> Page<Tenant>
ctl.find_tenant(external_ref) -> Option<Tenant>
ctl.get_tenant(id) / update_tenant(&key, id, &UpdateTenant) / delete_tenant(&key, id) -> Operation
ctl.create_database(&key, tenant, &CreateDatabase { name, home_region, durability, retention }) -> DatabaseInfo
ctl.list_databases(tenant, &ListOptions) / get_database(tenant, db) / delete_database(&key, tenant, db)
ctl.get_operation(id) / wait_operation(id, interval)
ctl.create_collection(&key, tenant, db, &CreateCollection { name, schema, ttl_path, unique, indexes, advisor }) -> CollectionInfo
ctl.list_collections / get_collection / update_collection(&key, .., &UpdateCollection) / delete_collection
ctl.create_key(&key, tenant, &CreateKey { name, grants, row_filter, rate, ttl_seconds }) -> KeyWithToken { key, token }
ctl.list_keys / get_key / revoke_key(&key, tenant, key_id) -> Key / rotate_key(&key, tenant, key_id, grace_seconds) -> KeyWithToken
ctl.list_regions(&ListOptions)
```

## Tests

`tests/v1_*.rs` run against an in-process HTTPS v1 fixture in
`tests/support/v1_fixture.rs`. `tests/v1_live.rs` runs against a real server
when `TB_V1_URL` and `TB_V1_CONTROL_KEY` are set (`TB_V1_CA_FILE` optionally
names a PEM root CA); otherwise it is skipped.
