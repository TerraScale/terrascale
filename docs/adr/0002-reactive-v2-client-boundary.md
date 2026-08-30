# ADR 0002: keep the v2 result engine separate from transport

- Status: Accepted for the current repository boundary
- Date: 2026-08-29
- Scope: `TerraScale.DotNet` reactive client

## Context

The repository contains two materially different v2 concerns:

- `TerraScaleSyncV2Protocol` and `TerraScaleSyncV2Client` provide the checked-in
  frame and checkpoint-session implementation used by the migration corpus and
  loopback HTTP/SSE tests; and
- `TerraScaleSyncV2ViewEngine` provides the result-view state machine, including
  atomic snapshots, result deltas, opaque checkpoints, authorization changes,
  and reversible optimistic presentation.

The view engine source explicitly owns no transport, route, framing, or
credential validation. The workspace goal requires a reviewed public result-view
contract and a shared semantic corpus before the replacement is called current.
Coupling the two surfaces now would require guessing a server contract and
would make migration evidence look like a released customer API.

## Decision

Keep the semantic result-view engine transport-independent. A future public
binding must be added only after its owning public contract, generated schema,
authorization behavior, limits, and cross-client corpus are available. The
binding must translate transport failures into the engine's typed protocol
errors without giving the engine private server context or a raw request escape
route.

The existing checkpoint session remains a separately documented implementation
record. Its passing tests are evidence for that implementation, not proof that
the result-view replacement is complete.

## Consequences

The engine can be tested deterministically without a server and cannot claim
transport conformance by itself. The eventual binding will need explicit
integration tests for authentication, reconnect, authorization changes,
history expiry, Region movement, slow consumers, tenant isolation, and
cross-language vectors. Until those artifacts exist, v2 production qualification
is blocked and no new route or payload is added here.
