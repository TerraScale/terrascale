# SDK production release gates

Status: Accepted as the SDK application of the workspace production goal.

Date: 2026-08-29

## Decision

The protected `develop` to `main` promotion may publish `@terrascale/sdk` only
after clean-install, typecheck, build, unit/contract, route and OpenAPI drift,
forbidden-surface, package-boundary, Node 22/24, supported-browser, and
MessagePack generation/hostile-input gates pass. Protected live DevHost and
release E2E must pass when required configuration is available. Missing live
configuration is a skipped gate, never a passing result.

Promotion also depends on workspace production gates: public REST and
reactive contract parity, Better Auth exact-version and license checks,
cross-language corpus conformance, authorization and recovery behavior,
three-Region qualification, security, operational readiness, and the required
production soak. The local SQLite runtime cannot satisfy any of those gates.

For reactive data, MessagePack carries the envelope and operation structure.
Document bodies remain raw TerraScale-canonical JSON bytes, and the client uses
one native `JSON.parse` per body after decoding the envelope.

The release workflow publishes the verified npm tarball. npm credentials stay
in protected jobs.

## Current state

The current Dashboard snapshot covers all 91 SDK routes with zero typed-contract
debt. The standalone MessagePack suites run through `npm test` and both
protected Node runtime lanes. Production WebSocket and HTTP fallback transport,
live Database API checks, and the current delivery workflow remain open tasks
in the workspace `GOAL.md`.
