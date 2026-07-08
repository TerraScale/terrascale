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

Normal releases are push-driven from `main`. The `Publish SDK to npm` Forgejo
workflow runs the preflight checks, resolves the next patch version from the
latest Forgejo Release, creates a draft Forgejo Release, updates the job-local
package version to that release version, publishes to npm, and publishes the
matching Forgejo Release.

Forgejo Releases are the intended version history. The workflow also compares
the latest npm registry version before choosing the next patch version so an
interrupted run that published to npm but failed before release finalization
publishes the matching Forgejo Release instead of bumping past that package
version.

The workflow executes `npm publish --access public --registry=https://registry.npmjs.org/`
with `NPM_TOKEN`.

The workflow intentionally writes npm credentials only to the job-local
`~/.npmrc`.
