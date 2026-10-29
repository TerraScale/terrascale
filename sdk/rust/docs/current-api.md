# Current TerraBase HTTP contract

This guide describes the SDK's implemented subset of the public v1 API.
The live [OpenAPI specification](https://api.terrascale.tech/openapi/v1.json),
retrieved on 2026-10-26, advertises 110 operations. That count is the server's
surface, not a claim that this SDK implements or qualifies every operation.
Buckets/files/rules, functions, metrics and export scheduling remain coverage gaps.
The [README](../README.md) and [document walkthrough](csharp-api.md) use current
project-scoped APIs; older native and dashboard examples describe retired APIs.

## Client and IDs

Use `Client::for_project_api_key(project_ulid, api_key)` for automatic exchange and refresh
([authentication guide](api-key-auth.md)), or `Client::for_project(project_ulid, access_token)`
for a static token, against `https://api.terrascale.tech`,
or `.endpoint(origin)` for a static endpoint. `Client::builder(origin, token)`
requires `.project(project_ulid)`. Public platform exploration uses
`Client::for_platform(origin, access_token)` and accepts `ts-auth`/`ts-dashboard`
selectors; it cannot issue unscoped document or transaction requests.

Entity IDs are canonical uppercase ULIDs. Database and collection selectors are
names. Document IDs are `DocumentId` (`ulid::Ulid`), sent in canonical uppercase;
responses with any other id form are rejected. Point routes use `/documents/by-key?id=`.
`ulid_for(collection, key)` derives a stable ULID from a natural key (first 16 bytes of
SHA-256 over `collection`, NUL, `key`).

## Public operations

Project clients expose database CRUD/placement/branch/copy, collection CRUD,
documents, transactions, queries, SQL/check/explain, schemas/inference,
constraints, indexes/recommendations/policy, saved queries, usage/analytics/
replication, backup destination/policy/snapshots/PITR, import lifecycle and
changes. Generic tooling methods accept serializable request bodies and decode
caller-selected response types, preserving all producer fields with `serde_json::Value`.
Recommendation selectors use `collection:field`, not entity IDs.

Restore acknowledgements use `RestoredDatabase` without a serving `region`.
Writable databases require a home region and at least one distinct replica region.
On create, omitting `replica_regions` uses all current serving regions; selecting
only a home region does not request single-region placement. An explicit empty
replica list on create returns HTTP 400.
`all_regions` (`allRegions`) asks for a full copy in every serving region, including
later regions; responses default it to `false` when absent. Nullable `home_region`/`replica_regions` preserve unassigned placement;
`replicas()` derives an empty slice for null replicas. `placement_version` is
an exact i64. Serving `ProjectDatabaseInfo` responses require `region`.

For deletion, call `delete_database` directly in the database's home region,
without a preceding placement update. Deletion covers every replica, including
all-regions placement. HTTP 204 confirms logical deletion and local cleanup;
remote erasure is retried durably and may complete later. Updating to an empty
replica list can temporarily disable document writes with HTTP 409
`placement_needs_two_regions`; it does not provide supported single-region placement.

## Errors and retries

`Error::Response` retains actual HTTP status, exact bounded response bytes and
request ID. Inspect `status()`, `code()`, `op()`, `request_id()`, `raw_body()` and
`is_uncertain()`. Both RFC 9457 and nested error envelopes are decoded.
Preconditions remain supported because the current producer implements them.
The SDK has no idempotency-key setters or automatic uncertain mutation replay.
An explicit repeated PUT is a new commit and can overwrite intervening edits.
Reads and explicitly read-only operations use bounded retries, including gateway
`502`/`503`/`504` answers without a TerraBase error code; only an actual
`429 rate_limited` or `429 tenant_busy` response permits a mutation retry after
rejected admission. `429 operation_allowance` is a quota refusal and is not retried.

## Validation

`tests/project_*.rs`, `tests/csharp_*_contract.rs`, shape/Atlas and transport tests
use HTTP fakes and producer-derived DTO fixtures. Endpoint status checks follow
handler returns, including HTTP 202 for updating an import. No producer binary
or live credentials are needed. CI/reviews remain the merge gate.
