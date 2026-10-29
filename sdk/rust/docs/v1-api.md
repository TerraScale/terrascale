# `terrascale::v1` API

The name-based TerraBase v1 client. Contract: terrabase `feat/v1-contracts`
at `7bc74f9d` (`docs/api-v1.md`, `query-v1.md`, `control-plane-v1.md`).
Reference implementation: ts-go `feat/v1-client` (`v1/`).

This page describes the original producer. For the C# producer's project
paths, bearer credentials, collection API and public API origin, see
[csharp-api.md](csharp-api.md). Its real-binary suite is independent of the
legacy test suite below. Fake fixtures never qualify a live producer.

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
Retry policy: `429 rate_limited` and `429 tenant_busy` always (after
`Retry-After`, capped); transport errors, the transient 503s (`capacity`,
`storage_unavailable`, `dependencies_unavailable`, `commit_uncertain`) and
uncoded gateway `502`/`503`/`504`/`429` answers only for reads, queries and
requests with an `Idempotency-Key`; `500 internal_error` only with a key. Other
429s, such as the `operation_allowance` quota refusal, surface as `Error::Api`. Oversized responses are never
retried; for a mutation answered with a non-4xx status they are uncertain
(`is_uncertain()`), since the write may have applied. `Retry-After` accepts
delay-seconds and HTTP-dates. Plain-HTTP and loopback base URLs ignore environment proxies.

## Documents

```rust
let users: Collection = db.collection("users");
users.get::<T>(id: DocumentId) -> Result<Document<T>>                     // 404 -> Error::NotFound { etag, .. }
users.get_if_changed::<T>(id, &Version) -> Result<Option<Document<T>>> // None = 304
users.put(id, &doc, WriteOptions) -> Result<WriteResult>            // created = 201
users.create(&doc, WriteOptions) -> Result<WriteResult>             // POST, server ULID
users.patch(id, &Patch, WriteOptions) -> Result<WriteResult>
users.delete(id, WriteOptions) -> Result<WriteResult>

WriteOptions::new().if_match(Version).if_none_match_any().idempotency_key(IdempotencyKey)
enum Precondition { IfMatch(Version), IfNoneMatchAny }
struct WriteResult { id: DocumentId, version: Version, offset: u64, durability: Durability, created: bool, location: Option<String> }
struct Document<T = Box<RawValue>> { id: DocumentId, version: Version, state: DocumentState<T> }
enum DocumentState<T> { Value(T), Conflict(Vec<Sibling<T>>) }
struct Sibling<T> { event: Event, deleted: bool, document: Option<T> }
Patch::merge(&v)? | Patch::ops(vec![PatchOp::set(p,&v)?, PatchOp::unset(p), PatchOp::inc(p, n), PatchOp::append(p,&v)?])
```

## Transactions

```rust
let mut tx = Tx::new();
tx.create(coll, Option<DocumentId>, &doc)
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
Filter::{and, or, not, eq, ne, lt, lte, gt, gte, in_, nin, exists, prefix, contains, within_radius, within_box}
Filter::within_radius("/loc", lat, lon, meters)   // {lat, lon} points, haversine meters 0..=20_000_000
Filter::within_box("/loc", south, west, north, east) // west > east crosses the antimeridian
Filter::from_json(r#"{"eq":["/email","a@b.c"]}"#)?
Path::from("/email") | Path::lower("/email") | Path::Id ("$id")
Query::new().filter(f).sort(SortKey::desc("/created_at")).project(["/email"]).limit(50).cursor(c).count(true).explain(Explain::Plan)
coll.query::<T>(&Query) -> Result<QueryPage<T> { items, next_cursor, count: Option<Count { value, exact }>, explain }>
coll.pages::<T>(Query) -> Pages<T>;  pages.next_page().await -> Result<Option<QueryPage<T>>>
coll.query_all::<T>(Query, max_items) -> Result<Vec<Document<T>>>
Query::new().filter(f).near(Near::new("/embedding", vec![0.1, 0.2]).metric(Metric::L2)).limit(10)
  // near is the only sort key: no sort/cursor, limit <= 100, Metric::{Cosine (default), L2};
  // each item gets document.distance: Option<f64>
Query::new().filter(f).geo_near(GeoNear::new("/loc", -23.55, -46.63)).limit(10)
  // same limits as near; document.distance is meters, documents without a point are skipped
Query::new().filter(f).aggregate(Aggregate::new().group_by(["/status"]).sum(["/amount"]).avg(["/amount"]).min(["/amount"]).max(["/amount"])).limit(100)
  // group_by 0-4 paths, sum/avg/min/max at most 16 distinct pointers; no sort/project/cursor/count;
  // limit bounds the groups. page.groups: Option<Vec<Group { key: Vec<Value>, count: u64,
  //   sum/avg/min/max: BTreeMap<String, Option<f64>> }>>, items is empty
coll.set_ttl("/expiresAt") -> Result<()>;  coll.get_ttl() -> Result<Option<String>>;  coll.clear_ttl() -> Result<()>
  // documents expire once the member holds a past RFC 3339 UTC time; an hourly sweep deletes them
```

## IDs

```rust
type DocumentId = ulid::Ulid  // canonical uppercase on the wire; non-canonical server ids are rejected
ulid_for(collection, key) -> DocumentId  // first 16 bytes of SHA-256(collection + "\0" + key)
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
documents, transactions, queries and revocation. Missing real-service inputs
must be reported as skipped, not successful qualification. Fake producers
belong only in test code/test binaries and cannot establish live compatibility.

For current C# producer qualification, use the immutable SHA-256-pinned real
release runner:

```sh
nice -n 19 python3 scripts/terrabase/run.py
```

See [csharp-api.md](csharp-api.md) for the producer revision and its remaining
JWT-only contract gap. This runner does not qualify the retired producer.
