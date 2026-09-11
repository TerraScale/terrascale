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

The CI workflow delegates to `ts-deploy` after checking out the event revision.
The portable repository gate is `make check`; browser and live API journeys are
separate lanes. For an SDK release, `ts-deploy` runs the checkout checks, invokes
`npm pack --json --ignore-scripts` exactly once into candidate staging, and passes
that exact absolute tarball to
`scripts/qualify-npm-artifact.ts --release --tarball`. Release qualification is
fail-closed: it requires a clean source tree, the reviewed npm installation, the
declared Node 22 and Node 24 executables, and an output receipt with no
limitations. It installs the supplied bytes into a clean consumer, checks exports,
secret and dependency boundaries, license and contract evidence, and verifies
that the input archive was not changed.

The resulting candidate archive and signature are uploaded to Forgejo and
verified before publication. Publication invokes `npm publish` on that same
tarball with lifecycle scripts disabled and requires exact registry readback;
release never rebuilds or repacks the checkout at publication time. A failed
qualification, changed archive, or unconfirmed registry outcome blocks handoff.
Commands, evidence requirements, and the still-unmet launch gates are maintained
in [the release guide](../release.md).
