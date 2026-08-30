# ADR 0004: owner-published public contract parity

Status: Accepted boundary; canonical route binding implemented, qualification pending.

Date: 2026-08-29

## Proposed decision

The .NET SDK implements only named operations published by Dashboard or
`TerraScale.Database`. Each operation binds method, versioned path or frame,
operation identity, parameters, media types, typed models, nullability,
security scope, limits, errors, partial outcomes, idempotency, retry,
pagination, checkpoints, and cancellation behavior.

The SDK route manifest and generated contract checks compare the package with
the owner schema. A missing or changed server operation blocks the affected
SDK member or requires a versioned compatibility decision. No generic request,
arbitrary path, custom header, private route, or direct engine path hides a
contract gap.

## Exit evidence

Every published member needs a parity record and success plus expected-failure
tests against the same schema revision. Breaking behavior requires an explicit
version and migration record.

## Compatibility disposition: unpublished saved-query versions

The SDK previously carried `/api/v2` conditional-revision and `/api/v3`
snapshot-pagination saved-query clients. Dashboard's OpenAPI, the TypeScript
route manifest, and the shared contract descriptor publish only the five
`/api/v1` saved-query operations. The unsupported clients entered the
repository in commits `3d9896ba1332e4bbbbc27e79816d5bc511910af2` and
`d5437a0d1bc4fb0413f5249f297dc39dc79657b1`. No release tag contains either
commit.

The unsupported clients, models, routes, and tests are therefore removed
before the `0.1.0` package candidate. `TerraScaleClient.SavedQueries` is the
only saved-query entry point. It binds list, create, get, update, and delete to
the owner-published `/api/v1` operations. Conditional revisions or snapshot
pagination require a new owner contract and compatibility record before an SDK
member may expose them.
