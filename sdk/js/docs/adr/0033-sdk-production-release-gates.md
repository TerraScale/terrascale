# SDK production release gates

Status: Accepted; publication is automatic.

Date: 2026-10-01

Require exact producer source/vector drift checks, clean install, typecheck,
declaration build, native codec conformance, maintained local Better Auth
semantics, actual framework lifecycle regressions, declared runtime boundaries,
supported Node and browser behavior, dependency audit and license evidence.

CI publishes to npm on every push to `main` once those checks pass, provided
the `package.json` version is not yet published. It packs one tarball and
publishes those exact bytes. Capabilities whose owning contracts are not yet
admitted (management/catalog provisioning, credential lifecycle, production
security authority) are not publication blockers; their entrypoints fail at
runtime with explicit capability errors. The artifact qualifier and promotion
script remain available for independent installed-artifact qualification.
