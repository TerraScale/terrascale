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
requests with an `Idempotency-Key`; `500 internal_error` only with a key. Only `429` with code `rate_limited` is
retried; other 429s surface as `Error::Api`. Oversized responses are never
retried; for a mutation answered with a non-4xx status they are uncertain
(`is_uncertain()`), since the write may have applied. `Retry-After` accepts
delay-seconds and HTTP-dates. Plain-HTTP and loopback base URLs ignore environment proxies.

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

Failures carry the op index: `err.op() -> Option<usize>`, for server errors and
for local validation (`Error::InvalidTxOp`).

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
    InternalError, Api { status, code, message, op, details },
    OperationFailed { id, code, message },   // ControlClient::wait_operation
    ResponseTooLarge { limit, status, may_have_applied }, // not retried; uncertain when may_have_applied
    UndecodableAck { status, message },     // 2xx mutation ack that failed to decode; uncertain
    InvalidTxOp { op, message },             // local Tx validation; err.op() == Some(op)
    Transport(String), InvalidInput(String), Decode(String),
}
err.status() / err.code() / err.op() / err.is_not_found() / err.is_uncertain()
// is_uncertain(): the mutation may have applied - commit_uncertain, internal_error,
// any other 5xx or non-4xx failure status, transport errors, undecodable or
// mismatched 2xx acks (including tx results that do not match the ops), and
// oversized non-4xx responses to mutations. 4xx and local errors are certain.
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
ctl.get_operation(id) / wait_operation(id, interval)   // failed -> Error::OperationFailed
ctl.create_collection(&key, tenant, db, &CreateCollection { name, schema, ttl_path, unique, indexes, advisor }) -> CollectionInfo
ctl.list_collections / get_collection / update_collection(&key, .., &UpdateCollection) / delete_collection
ctl.create_key(&key, tenant, &CreateKey { name, grants, row_filter, rate, ttl_seconds }) -> KeyWithToken { key, token }
ctl.list_keys / get_key / revoke_key(&key, tenant, key_id) -> Key / rotate_key(&key, tenant, key_id, grace_seconds) -> KeyWithToken
ctl.list_regions(&ListOptions)
```

## Tests

Fast suite (`cargo test --test v1_fast`): the client against an in-process
HTTPS (TLS 1.3, throwaway CA) contract fixture in `tests/support/v1_fixture.rs`,
ported from ts-go `v1/fixture_*_test.go`. It covers CRUD, 412, 304, 404 with
the tombstone ETag, the `If-None-Match: *` create race (one winner), unique
409, conflicts, merge and ops patches, transactions with op-indexed errors,
idempotent replay and uncertain commits, the retry policy, query paging,
counts and projection, grants, and control-plane provisioning, rotation and
revocation.

Slow suite (`cargo test --test v1_live`): runs against a real server when
`TB_V1_URL` and `TB_V1_CONTROL_KEY` (a token, or a file holding one) are set;
`TB_V1_CA_FILE` optionally names a PEM root CA. Without them it is skipped.
It provisions its own tenant, database, collection and key, then exercises
documents, transactions, queries and revocation. For local development it
also runs against ts-go's `tbv1-fake` (branch `feat/v1-fake`):

```sh
tbv1-fake --listen 127.0.0.1:9465 --cert-dir .tbv1 --control-key-out .tbv1/control.key &
TB_V1_URL=https://localhost:9465 TB_V1_CONTROL_KEY=.tbv1/control.key \
TB_V1_CA_FILE=.tbv1/ca.pem cargo test --test v1_live
```
