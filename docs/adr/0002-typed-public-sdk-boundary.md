# Typed public SDK boundary

Status: Accepted. Production parity requires live qualification.

## Decision

Expose named typed operations through the package's declared exports.
TerraScale.Database publishes database and reactive contracts; Dashboard
publishes its product contracts. Each operation binds method or frame,
versioned path, parameters,
media types, request/response shape, authorization, errors, limits, retry,
idempotency, pagination, checkpoints, and cancellation.

Keep HTTP transport private, including at JavaScript runtime through `#http`
fields. Consumers cannot reach arbitrary paths, custom headers, private TBEP,
or Dashboard-only shortcuts. A missing operation requires an owner-published
public contract; a generic request method cannot conceal the gap.

## Qualification

Typed route capture and OpenAPI descriptor/drift checks are part of release
admission. A successful TypeScript build alone does not prove parity. The
committed route manifest and OpenAPI descriptor pin 73 routes against the
TerraBase and edge contract bundles. The saved-query CRUD operations use the
owner-published v1 paths, page envelope, continuation offset, and
revision-protected update contract. Queue operations are removed, and the
forbidden-surface check rejects queue routes and clients.

## Enforcement

`src/public-boundary.test.ts`, `src/public-schema-parity.test.ts`,
`test/forbidden-surface.test.ts`, and
`scripts/generate-client-route-manifest.ts` enforce the package boundary.
The owning service contract and SDK checks must agree before production.
