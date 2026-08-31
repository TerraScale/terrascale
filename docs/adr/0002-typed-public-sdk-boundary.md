# Typed public SDK boundary

Status: Accepted for the current package boundary. Production contract parity
remains a release gate.

Date: 2026-08-29

## Context

The SDK is consumed by Dashboard and customer applications. A generic request
helper would let callers bypass operation ownership, schema review,
authorization, idempotency, retry, and route-manifest checks. The SDK must not
become a private Database API or TerraBase client.

## Decision

Expose named typed operations from `@terrascale/sdk` and
`@terrascale/sdk/admin`. Keep the HTTP transport private to the package. Each
operation must identify its method, versioned path, parameters, media types,
typed request and response shapes, errors, security, retry/idempotency rules,
and pagination or checkpoint behavior from an owner-published contract.

The package has no generic request entrypoint, consumer-selected arbitrary
headers, direct TerraBase/TBEP path, or Dashboard-private route. A missing
public operation stays missing until its owning service publishes the contract.

## Consequences

Typed route capture and OpenAPI descriptor/drift checks are part of release
admission. A successful TypeScript build alone does not prove parity. The
current package implements 91 captured routes, and the current Dashboard
snapshot covers all 91. The saved-query CRUD operations use the owner-published
v1 paths, page envelope, continuation offset, and revision-protected update
contract. Project queue operations use the Dashboard-owned project-scoped
OpenAPI contract and retain typed acknowledgement results plus explicit
regional affinity. The legacy global queue API remains excluded.

## Enforcement

`src/public-boundary.test.ts`, `src/public-schema-parity.test.ts`,
`scripts/check-forbidden-surface.mjs`, and
`scripts/generate-client-route-manifest.mjs` enforce the package boundary.
The owning service contract and SDK checks must agree before production.
