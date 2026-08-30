# ADR 0004: owner-published public contract parity

Status: Accepted boundary; implementation and qualification pending.

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
