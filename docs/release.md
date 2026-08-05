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
npm pack --pack-destination artifacts/candidate
```

`test:integration` must use real `TERRASCALE_API_BASE_URL` and
`TERRASCALE_AUTH_API_BASE_URL` values when validating a release candidate. The
publish workflow fails closed when those variables are absent instead of using
mocked HTTP.

Each release-candidate test and package job performs its own clean install and
build before testing or packing; do not rely on artifacts from another job.

## Publish

Normal releases are built from the protected `develop` to `main` release pull request. The `Publish SDK to npm` Forgejo
workflow validates the Documentation contract fixture before candidate creation,
confirms the committed SDK version is not yet present on the public npm registry,
then packages it. This is an availability preflight, not a reservation. After `main` receives that tested tree,
the trusted promotion job verifies the candidate manifest, tarball digest,
package name, and version, repeats the public availability check, then publishes
that exact tarball to npm. The promotion job has the npm token; pull-request jobs
do not. npm rejects an existing name/version rather than overwriting a published
package if another publisher wins the race.

Forgejo Releases are the intended version history. The workflow compares the
latest npm registry version so an interrupted run that published to npm but
failed before release finalization publishes the matching Forgejo Release
instead of bumping past that package version.

## Version classification

Use the next patch version only for backward-compatible changes. Before `1.0`,
a source-breaking exported TypeScript type change requires the next minor
version. This release is `0.2.0`: `PricingPlan.key`, `PricingPlan.visible`, and
all provider price/product identifier fields are now required (nullable where
the public API permits `null`); `PricingPlan.provider` is the public string
value. Callers that construct a `PricingPlan` must provide every required field.

The trusted main-promotion job executes `npm publish <verified-tarball> --access public
--ignore-scripts --registry=https://registry.npmjs.org/` with a job-local `NPM_TOKEN`.

The workflow writes npm credentials only to a temporary npm configuration in
the trusted promotion job.
