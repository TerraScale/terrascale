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

Promotion also depends on the workspace production evidence: public REST and
reactive contract parity, Better Auth exact-version and license evidence,
cross-language corpus conformance, authorization and recovery behavior,
three-Region qualification, security, operational readiness, and the required
production soak. The local SQLite runtime cannot satisfy any of those gates.

The workflow publishes the verified npm tarball and does not create or repair a
Forgejo Release. npm credentials and candidate-store access stay in protected
jobs.

## Current blockers

The current Dashboard snapshot leaves one SDK route uncovered, Safari is
host-blocked in this environment, no live API evidence was available, and the
standalone MessagePack suites are not yet part of the release profile. See
`GOAL.md` TS-001, TS-006, TS-007, and TS-009.
