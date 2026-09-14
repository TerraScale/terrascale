# SDK production release gates

Status: Accepted. Live release qualification remains required.

## Decision

Release from `develop` only after contract drift, clean install, typecheck,
build, unit/conformance, package boundaries, supported Node/browser, and hostile
MessagePack checks pass. Qualify the exact npm tarball in a clean consumer and
publish those same bytes with a source revision, artifact hash, and successful
CI check record. Broader compliance attestations are post-launch work.
Keep publication credentials out of candidate inspection.

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
