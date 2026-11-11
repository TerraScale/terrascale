# Current TerraBase HTTP contract

This guide describes the SDK's implemented subset of the public v1 API. The live
[OpenAPI specification](https://api.terrascale.tech/openapi/v1.json) is the server's surface,
not a claim that this SDK implements or qualifies every operation. SQL follows the shared
[SQL SDK API shape](../../SQL-API.md).

## Client and IDs

Use `Client::for_project_api_key(project_ulid, api_key)` for automatic exchange and refresh
([authentication guide](api-key-auth.md)), or `Client::for_project(project_ulid, access_token)`
for a static token, against `https://api.terrascale.tech`,
or `.endpoint(origin)` for a static endpoint (with an API key, an endpoint other than
`https://api.terrascale.tech` also needs `.auth_base_url(origin)`, the ts-auth that exchanges it).
`Client::builder(origin, token)`
requires `.project(project_ulid)`. Platform administration uses
`Client::for_platform(origin, access_token)`.

Entity IDs are canonical uppercase ULIDs; `new_id()` generates one and `is_valid_id(id)` checks one. Database selectors are a
name or ULID.

## Public operations

Project clients expose SQL (`Database::sql`), database create/get/list/rename/branch/delete,
saved queries, usage and analytics, backup destination/policy/snapshots/PITR, request logs,
metrics, file buckets and files, and functions. Generic methods accept serializable request
bodies and decode caller-selected response types, preserving all producer fields with
`serde_json::Value`.

Each database lives in one home region. `DatabasePlacement::home_region` selects it on create;
without it the called region is the home. Restore acknowledgements use `RestoredDatabase`
without a serving `region`; `placement_version` is an exact i64. Serving `ProjectDatabaseInfo`
responses require `region`.

Call `delete_database` in the database's home region. HTTP 204 confirms logical deletion and
local cleanup; remote erasure is retried durably and may complete later.

## Errors and retries

`Error::Response` retains actual HTTP status, exact bounded response bytes and
request ID. Inspect `status()`, `code()`, `sql()`, `request_id()`, `raw_body()` and
`is_uncertain()`. Both RFC 9457 and nested error envelopes are decoded.
The SDK has no idempotency-key setters or automatic uncertain mutation replay.
Reads and explicitly read-only operations use bounded retries, including gateway
`502`/`503`/`504` answers without a TerraBase error code; only an actual
`429 rate_limited` or `429 tenant_busy` response permits a mutation retry after
rejected admission. `429 operation_allowance` is a quota refusal and is not retried.

## Validation

The monorepo's journey runs the Rust runner (`e2e/sdk/rust`) against the TerraBase built from the same commit.
