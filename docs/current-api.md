# Current TerraBase HTTP contract

Source snapshot: TerraScale/terrascale `e7fa366ddb289f5612c93d316b316fb92c568151`.
Public scope is `docs/terrabase/api.md` plus `ApiReferenceExtensions.ShouldInclude`:
76 mapped routes, including public project management, probes and platform explore.
Hidden identity/dashboard aliases, internal/admin routes and hidden PUT database
are outside this contract. No mapped `downloadBackup` endpoint exists.

Historical README and older guides are retained verbatim under the shared-doc
policy. Their native, unscoped and dashboard examples describe retired APIs;
those exports, HTTP clients and executable qualification helpers are deleted.
Use this guide for the current SDK. Tests use HTTP fakes only, never producer binaries.

## Client and IDs

Use `Client::for_project(project_ulid, access_token)` against `https://api.terrascale.tech`,
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
`all_regions` (`allRegions`) asks for a full copy in every serving region, including
later regions; responses default it to `false` when absent. Nullable `home_region`/`replica_regions` preserve unassigned placement;
`replicas()` derives an empty slice for null replicas. `placement_version` is
an exact i64. Serving `ProjectDatabaseInfo` responses require `region`.

## Errors and retries

`Error::Response` retains actual HTTP status, exact bounded response bytes and
request ID. Inspect `status()`, `code()`, `op()`, `request_id()`, `raw_body()` and
`is_uncertain()`. Both RFC 9457 and nested error envelopes are decoded.
Preconditions remain supported because the current producer implements them.
The SDK has no idempotency-key setters or automatic uncertain mutation replay.
An explicit repeated PUT is a new commit and can overwrite intervening edits.
Reads and explicitly read-only operations use bounded retries; only an actual
`429 rate_limited` response permits a mutation retry after rejected admission.

## Validation

`tests/project_*.rs`, `tests/csharp_*_contract.rs`, shape/Atlas and transport tests
use HTTP fakes and producer-derived DTO fixtures. Endpoint status checks follow
handler returns, including HTTP 202 for updating an import. No producer binary
or live credentials are needed. CI/reviews remain the merge gate.
