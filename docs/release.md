# SDK release qualification

`@terrascale/sdk` is published to the public npm registry. The repository uses
one `main` branch. Its [CI workflow](../.forgejo/workflows/ci.yml) calls the
central `ts-deploy` reusable workflow, which checks out the event revision,
installs the version-pinned `ts-deploy` binary, and runs
`ts-deploy ci --repo ts-typescript` on the `terrascale-builder` runner. There is
no manual promotion or pull-request release workflow.

## Implemented checks and publication

[Makefile](../Makefile) runs route/OpenAPI drift checks, a clean install,
typecheck, build, and Vitest. Browser tests and artifact qualification exist
as separate commands; `make check` does not run them.

The `ts-deploy` npm path packs once, passes the candidate tarball to this
repository's qualifier, verifies its hash after qualification, and publishes
that same tarball with lifecycle scripts disabled. Failed qualification or
changed bytes stop publication. Default candidate outputs use the ignored
`artifacts/` directory so qualification can verify clean source.

This is the launch verification minimum. Browser, clean-consumer, and real-API
qualification still need passing results. Broader compliance attestations are
post-launch work and must not become an alternative release pipeline.

## Qualification procedure

Run the repository gate and browser suite:

```bash
make check
npm run test:browser
```

For diagnostic package qualification with explicit supported Node executables:

```bash
npm run qualify:artifact -- --node /path/to/node22 --node /path/to/node24
```

Release mode reads exact executable paths from `TERRASCALE_NODE_22` and
`TERRASCALE_NODE_24`, and verifies `TERRASCALE_NPM_ARCHIVE_INTEGRITY` against
[scripts/npm-release-policy.ts](../scripts/npm-release-policy.ts). Run it with
the declared Node 24 executable and reviewed npm installation:

```bash
npm run qualify:artifact -- --release --tarball /absolute/path/to/candidate.tgz --output /absolute/path/to/qualification
```

Release qualification refuses dirty source, unsupported or mismatched
runtimes, an existing output directory, and `--allow-dirty`. With `--tarball`,
it copies the supplied bytes without repacking, installs them into a clean
consumer, and verifies the original stayed unchanged. Without `--tarball`,
it creates a new diagnostic candidate with `npm pack`.

The qualifier checks exports, scans packed contents for secrets, audits
production dependencies, and verifies license and pinned contract evidence.
The Better Auth export supports explicit `node` and `workerd` server
conditions and rejects browser resolution. Its Workerd import graph contains
no Node-only modules. A Node process selecting the Workerd export verifies
package resolution; the real dashboard Worker journey remains separate
runtime evidence.

The record binds source, tarball, route/OpenAPI digests, Node executables,
npm installation, and results. Diagnostic receipts and receipts with
limitations cannot admit a release. npm publication is immutable. An
interrupted publish has an unknown outcome until the registry and artifact
records are inspected.

## Evidence required before public release

- Contract generation, hostile decoding, and shared semantic/byte conformance
  against the pinned producer contract, including all four customer SDKs.
- Node 22/24, browser, clean-consumer, public export, and server-only boundary
  checks on the exact package. Record actual browser engines and versions;
  a Playwright project name alone does not establish branded browser coverage.
- Real API success and denial, tenant isolation, retries, permission changes,
  Region movement, reconnect, and expired-history behavior.
- Better Auth exact-version, license, concurrency, migration, one-time consume,
  revocation, and rollback evidence through its real public operation port.
- Workspace launch qualification, security, operations, billing, and soak gates.

Live tests require their documented API URL, access token, and project or
organization identifiers. Missing configuration is a skipped gate and blocks
required release evidence. Keep credentials out of logs and receipts.

## Versions

The package version comes from `package.json`. Before 1.0, a source-breaking
exported TypeScript change requires a minor version; compatible fixes use a
patch. At launch, public contract support follows the workspace compatibility
decision. Regenerate and qualify consumers when the producer contract changes.
