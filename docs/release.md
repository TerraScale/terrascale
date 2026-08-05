# SDK Release Process

`@terrascale/sdk` publishes to the public npm registry only. Do not publish SDK
artifacts to Forgejo package registries.

## Preflight

Run these checks from the repository root:

```bash
npm ci
npm run typecheck
npm run build
npm run test:integration
```

`test:integration` must use real `TERRASCALE_API_BASE_URL` and
`TERRASCALE_AUTH_API_BASE_URL` values when validating a release candidate. The
publish workflow fails closed when those variables are absent instead of using
mocked HTTP.

## Publish

Normal releases are built from the protected `develop` to `main` release pull request. The `Publish SDK to npm` Forgejo
workflow runs the preflight checks, creates a draft Forgejo Release, updates
the job-local package version to the selected release version, publishes to
npm, and publishes the matching Forgejo Release. The committed `package.json`
and `package-lock.json` declare that selected version.

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

The workflow executes `npm publish --access public --registry=https://registry.npmjs.org/`
with `NPM_TOKEN`.

The workflow intentionally writes npm credentials only to the job-local
`~/.npmrc`.
