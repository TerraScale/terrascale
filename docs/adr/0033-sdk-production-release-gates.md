# SDK production release gates

Status: Mandatory source, artifact and actual runtime qualification.

Require exact producer source/vector drift checks, clean install, typecheck,
build, native codec conformance, maintained local Better Auth semantics, actual
framework lifecycle regressions, declared runtime boundaries, supported Node
and browser behavior, dependency audit and license evidence. Install one exact
npm tarball independently and qualify that artifact against a real allocation.

Management/catalog provisioning, credential lifecycle, production security
authority and deployed authenticated account/browser journeys remain required
blockers. Local SQLite and synthetic transport fixtures do not satisfy them.
Release-E2E must fail when owning capabilities or configuration are unavailable.

The artifact qualifier retains blockers in its report. Promotion verifies an
admitted receipt against source identity and output bytes and refuses active
blockers. Native CI does not publish and never receives npm publication secrets.
