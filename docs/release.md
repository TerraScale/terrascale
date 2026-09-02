# SDK Release Process

`@terrascale/sdk` publishes to the public npm registry only. Do not publish SDK
artifacts to Forgejo package registries.

## Preflight

Run these checks from the repository root:

```bash
npm ci
npm run typecheck
npm run build
npm run test:unit
npm run test:msgpack
npm run test:integration
npm run test:browser
npm run qualify:artifact -- --node /path/to/node22 --node /path/to/node24
```

`qualify:artifact` without `--release` is diagnostic. The protected build
uses `--release --output artifacts/candidate` and supplies the exact
Node v22.23.2 and v24.20.0 executable paths provisioned by the commit-pinned
setup action. Release mode packs once, installs that exact tarball into a clean consumer,
checks the public export graph and Node-only boundaries, scans the archive for
credential material, and writes a digest-, source-tree-, dependency-, license-,
audit-, contract-, and runtime-bound schema-2 receipt beside the tarball. Both
supported Node majors must be supplied for release evidence. A one-runtime
receipt is diagnostic and records the missing runtime as a limitation. The
command refuses a dirty worktree unless `--allow-dirty` is explicitly supplied;
dirty receipts are diagnostic and bind the uncommitted path list only by digest.
Receipt schema version 2 also binds the exact packed route manifest, reviewed
OpenAPI descriptor, Dashboard OpenAPI SHA-256, exact Node executables, and the
absence of release limitations. It binds npm 11.18.0 by reviewed archive
integrity, CLI entrypoint digest, package manifest digest, and a digest of every
regular file in the npm package tree. `.tools/` is explicitly ignored and
recorded as workflow tooling; it is never classified as candidate source.

The generated public MessagePack binding is qualified by the named test script:

```bash
npm run test:msgpack
```

`npm test` and both protected Node runtime lanes run this script, so drift,
hostile decoding, corpus round trips, and result-view semantics cannot be
omitted from a candidate test run.

The workflow also runs the unit and contract suite on Node.js 22 and 24 and
the browser contract suite on the current pinned Playwright Chromium, Firefox,
and WebKit engines. Update Playwright at qualification time so its engine
revisions match the release record. Record branded Chrome, Safari, and Edge
results separately when the protected browser lane runs them. Missing evidence
does not become a passing result.

The release E2E profile fails before tests run unless real
`TERRASCALE_API_BASE_URL`, `TERRASCALE_E2E_ACCESS_TOKEN`,
`TERRASCALE_E2E_PROJECT_ID`,
`TERRASCALE_E2E_ORGANIZATION_ID`, and
`TERRASCALE_E2E_SAVED_QUERIES_PROJECT_ID` are all configured. These values are
scoped only to the protected post-merge `main` release E2E job; no pull-request
job, including a same-repository `develop` to `main` PR, receives them. The API
endpoint must be an `http` or `https` URL, and each resource identifier must be
a valid ULID.

Each release-candidate test and package job performs its own clean install and
build before testing or packing; do not rely on artifacts from another job.

Release admission runs strict OpenAPI drift validation. The normal-CI debt
baseline does not permit promotion: unresolved entries stop production release
until the owning service publishes the matching typed schemas. Dashboard owns
identity and product routes; the independently deployable `TerraScale.Database`
service, sourced permanently in `TerraScale.Internals`, owns database and
reactive routes.
It also regenerates and compares the committed comprehensive OpenAPI descriptor;
operation IDs, parameters, request contracts, and every 2xx response shape must
match the reviewed pin before the trusted candidate can be created. Descriptor
version 2 records the SHA-256 of the exact Dashboard OpenAPI bytes and the exact
SDK route-manifest bytes, so a replacement or reformat requires explicit
regeneration and review even when the parsed operations are unchanged.

The OpenAPI snapshot is owned by Dashboard. If the snapshot and SDK disagree,
stop and resolve ownership or refresh the snapshot. Do not add a local debt
baseline entry for an unreviewed route.

## Publish

Normal releases merge the protected `develop` to `main` promotion PR. Only the
protected post-merge `main` workflow validates the consolidated public
OpenAPI contract fixture,
confirms the committed SDK version is not yet present on the public npm registry,
packages the candidate, and uploads the tarball, qualification receipt, and
Deploy build receipt with commit-pinned Forgejo artifact actions. This is an
availability preflight, not a reservation. A credentialless candidate job
verifies the complete file set, both receipts, Git commit and tree, descriptor,
route manifest, package/version, tarball digests, exact Node runtimes, and empty
limitations before the promotion job can receive npm credentials. Promotion
downloads the admitted immutable workflow artifact, verifies it again, repeats
the public availability check, then publishes that exact tarball to npm. Every
candidate verifier and promotion job provisions Node v24.20.0 through the
commit-pinned setup action and extracts npm 11.18.0 directly from the reviewed
SHA-512-bound registry archive. Verification rejects a different Node binary,
npm entrypoint, npm package manifest, or npm package tree. Registry reads and
publication execute the verified `bin/npm-cli.js` with the exact Node binary;
they never fall back to an ambient `npm` command.
Pull-request jobs targeting either `develop` or
`main` run the unprivileged static, build, and contract checks. They never
receive publish or signing credentials and cannot create
candidate artifacts. The required `promotion/admission` status accepts only a
canonical-repository `develop` to `main` pull request. npm
rejects an existing name/version rather than overwriting a published package if
another publisher wins the race.

This workflow does not create or finalize a Forgejo Release. Its implemented
promotion sequence is candidate verification followed by publishing the
verified tarball to npm. If npm publishing is interrupted or its result is
uncertain, operators must inspect npm and the immutable workflow-artifact records before
any recovery action; do not assume this workflow will create or repair a
Forgejo Release.

Deploy CLI 1.0.80 predates `ci build-candidate` and its build-receipt v1
contract. Do not claim this release path is operational until an authorized
new CLI version is published, verified in a clean consumer, and pinned exactly.
The current protected Deploy validator also rejects the SDK's source-command
release profiles because it has no npm release-profile handoff. That failure is
intentional; do not weaken it or model the SDK as a TUnit apphost.

The local SQLite runtime and emulator tests are development evidence only. They
cannot satisfy live API, authorization, multi-Region, recovery, or performance
gates. Integration tests that lack their required environment skip and must be
recorded as skipped in the release receipt.

## Version classification

Use the next patch version only for backward-compatible changes. Before `1.0`,
a source-breaking exported TypeScript type change requires the next minor
version. This release is `0.3.0`. It removes the dedicated Auth, project
credential, payment, billing, pricing, and project authentication-settings
clients, models, routes, and local-emulator implementations. Consumers must stop
calling those removed namespaces before upgrading from `0.2.x`. This release
also applies the workspace M0 launch cuts (GOAL.md D-40): the retired product
surfaces and their repository-scoped data clients are deleted, and the data
path returns with the Rust public API. Generic bearer authorization for the
remaining public APIs and Sync authentication remain.

The trusted main-promotion job executes the verified npm CLI with `publish
<verified-tarball> --access public --ignore-scripts
--registry=https://registry.npmjs.org/` and a job-local `NPM_TOKEN`.

The workflow writes npm credentials only to a temporary npm configuration in
the trusted promotion job. The file mode is `0600`, and the verifier removes
`NPM_TOKEN` from its inherited environment before inspecting any candidate
bytes or invoking npm.
