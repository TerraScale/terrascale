# SDK release qualification

`@terrascale/sdk` is published to the public npm registry. The repository uses
one `develop` branch. Its [CI workflow](../.forgejo/workflows/ci.yml) checks out
the event revision and calls `ts-deploy ci --repo ts-typescript` on the
`terrascale-builder` runner. There is no `main` promotion or pull-request release
workflow.

## Implemented checks and publication

[Makefile](../Makefile) runs route/OpenAPI drift checks, a clean install,
typecheck, build, and Vitest. Browser tests and artifact qualification exist
as separate commands; `make check` does not run them.

The current `ts-deploy` path archives build outputs but publishes npm from the
checkout. It does not yet prove that the tested package and published bytes
are identical. The [workspace deployment goal](../../GOAL.md#ts-deploy) requires
packing once, checking that tarball, and publishing the same bytes with scripts
disabled. This remains implementation work before release.

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
npm run qualify:artifact -- --release --output artifacts/candidate
```

The optional diagnostic qualifier in release mode refuses dirty source, unsupported or mismatched runtimes, an
existing output directory, and `--allow-dirty`. It packs once, installs that
tarball into a clean consumer, checks exports and browser/server boundaries,
scans packed contents for secrets, audits production dependencies, and verifies
license and pinned contract evidence. The diagnostic record binds source,
tarball, route/OpenAPI digests, Node executables, npm installation, and results.
Diagnostic receipts and receipts with limitations cannot admit a release.

The optional qualifier is not the publication path and its detailed receipt
is not an additional launch requirement. Production publication must preserve
the tested artifact, isolate npm credentials, and refuse changed bytes. npm
publication is immutable. An interrupted publish has an unknown outcome until
the registry and artifact records are inspected.

## Evidence required before public release

- Contract generation, hostile decoding, and shared semantic/byte conformance
  against the pinned Rust producer, including all four customer SDKs.
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
