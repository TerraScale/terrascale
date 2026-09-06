# SDK production release gates

Status: Accepted. Live release qualification remains required.

## Decision

Release from `develop` only after contract drift, clean install, typecheck,
build, unit/conformance, package boundaries, supported Node/browser, and hostile
MessagePack checks pass. Qualify the exact npm tarball in a clean consumer and
publish those same bytes with a source revision, artifact hash, and successful
CI check record. Broader compliance attestations are post-launch work.
Keep publication credentials out of candidate inspection.

Required live API, Better Auth, cross-language, authorization, recovery,
three-Region, security, operational, and soak evidence must pass before public
launch. Missing live configuration is a skipped gate, never a passing result.

## Implementation boundary

The current workflow delegates to `ts-deploy`; `make check` runs the portable
repository gate. Browser and tarball qualification commands exist separately.
The deployment CLI still publishes npm from the checkout. Pack-once publication
of the exact verified tarball remains required work in the
[workspace goal](../../../GOAL.md#ts-deploy). Commands and qualification
requirements are maintained in [the release guide](../release.md).
