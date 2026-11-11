# SDK release process

The release artifact is one npm tarball, `@terrascale/sdk`. `scripts/qualify-release.sh`
builds it: it requires Node 24.21.0 and a `0.0.X` version, provisions the reviewed npm
CLI with `scripts/provision-reviewed-npm.js` and records its evidence, audits every
committed npm graph (`npm run check:vulnerabilities`), installs the locked tree with
`npm ci`, type-checks, builds the type declarations, checks the route manifests
(`npm run check:v1-route-manifest`) and packs the tarball. Run it at low CPU priority
with an empty output directory outside the checkout:

```sh
nice -n 19 bash scripts/qualify-release.sh /tmp/ts-js-release
```

On pull requests and main pushes that change `sdk/js`, the `sdk (sdk-js)` leg of
`.github/workflows/ci.yml` runs `make sdk-js-check`: the locked install, the type-check
and the build.

## Publishing

Pushing the tag `sdk/js/v0.0.N` on a `main` commit starts
`.github/workflows/sdk-release.yml`. Its `qualify` job runs the qualification above at
the tag and requires the tag to match the `package.json` version. Its `npm` job then
publishes that tarball with `npm publish --ignore-scripts --provenance --access public`,
authenticated by the `NPM_TOKEN` Actions secret; without it the job fails. Never publish
secret values or private storage paths.
