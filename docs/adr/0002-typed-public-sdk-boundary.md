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

Generate route and OpenAPI metadata from the exact producer contract pin.
Route capture, public-schema parity, and built-package boundary tests must
agree with the exported operations. A route count or successful typed build
alone is not parity evidence. Qualify success and expected failure against
real public services before release. See [release qualification](../release.md).
