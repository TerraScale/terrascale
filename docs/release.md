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
`TERRASCALE_AUTH_API_BASE_URL` values when validating a release candidate. If
those variables are absent, the smoke test skips instead of using mocked HTTP.

## Publish

1. Confirm `package.json` has the intended semver version.
2. Create and push a matching tag, for example `v0.1.0`.
3. The `Publish SDK to npm` Forgejo workflow runs the preflight checks and then
   executes `npm publish --access public --registry=https://registry.npmjs.org/`
   with `NPM_TOKEN`.

The workflow intentionally writes npm credentials only to the job-local
`~/.npmrc`.
