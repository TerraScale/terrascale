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
npm run test:integration
npm run test:browser
npm run qualify:artifact -- --node /path/to/node22 --node /path/to/node24
```

`qualify:artifact` packs once, installs that exact tarball into a clean consumer,
checks the public export graph and Node-only boundaries, scans the archive for
credential material, and writes a digest-, source-tree-, dependency-, license-,
audit-, and runtime-bound receipt under `artifacts/npm-qualification`. Both
supported Node majors must be supplied for release evidence. A one-runtime
receipt is diagnostic and records the missing runtime as a limitation. The
command refuses a dirty worktree unless `--allow-dirty` is explicitly supplied;
dirty receipts are diagnostic and bind the uncommitted path list only by digest.

The generated public MessagePack binding is qualified by additional commands
because the current npm `test` script does not discover `test/*.mjs`:

```bash
node scripts/generate-msgpack-protocol.mjs --check
node --test test/msgpack-manifest-drift.test.mjs
node --test test/msgpack-hostile-decode.test.mjs
node --test test/msgpack-corpus-roundtrip.test.mjs
node --test test/sync-v2-native-corpus-view.test.mjs
```

These commands are required release evidence today. The protected workflow
does not yet run them automatically, which is tracked as TS-006 in `GOAL.md`.

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
match the reviewed pin before the trusted candidate can be created.

The OpenAPI snapshot is owned by Dashboard. If the snapshot and SDK disagree,
stop and resolve ownership or refresh the snapshot. Do not add a local debt
baseline entry for an unreviewed route.

## Publish

Normal releases merge the protected `develop` to `main` promotion PR. Only the
protected post-merge `main` workflow validates the consolidated public
OpenAPI contract fixture,
confirms the committed SDK version is not yet present on the public npm registry,
packages the candidate, and uploads it to the candidate store. This is an
availability preflight, not a reservation. The promotion job depends on that
same trusted candidate job and verifies the candidate manifest, tarball digest,
package name, and version, repeats the public availability check, then publishes
that exact tarball to npm. Pull-request jobs targeting either `develop` or
`main` run the unprivileged static, build, and contract checks. They never
receive candidate-store, publish, or signing credentials and cannot create
candidate artifacts. The required `promotion/admission` status accepts only a
canonical-repository `develop` to `main` pull request. npm
rejects an existing name/version rather than overwriting a published package if
another publisher wins the race.

This workflow does not create or finalize a Forgejo Release. Its implemented
promotion sequence is candidate verification followed by publishing the
verified tarball to npm. If npm publishing is interrupted or its result is
uncertain, operators must inspect npm and the candidate-store records before
any recovery action; do not assume this workflow will create or repair a
Forgejo Release.

The local SQLite runtime and emulator tests are development evidence only. They
cannot satisfy live API, authorization, multi-Region, recovery, or performance
gates. Integration tests that lack their required environment skip and must be
recorded as skipped in the release receipt.

## Version classification

Use the next patch version only for backward-compatible changes. Before `1.0`,
a source-breaking exported TypeScript type change requires the next minor
version. This release is `0.3.0`. It removes the dedicated Auth, project
credential, queue, payment, billing, pricing, and project authentication-settings
clients, models, routes, and local-emulator implementations. Consumers must stop
calling those removed namespaces before upgrading from `0.2.x`. Generic bearer
authorization for the remaining public APIs and Sync authentication remain.

The trusted main-promotion job executes `npm publish <verified-tarball> --access public
--ignore-scripts --registry=https://registry.npmjs.org/` with a job-local `NPM_TOKEN`.

The workflow writes npm credentials only to a temporary npm configuration in
the trusted promotion job.
