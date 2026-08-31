# SDK release process

`@terrascale/sdk` publishes to the public npm registry. Forgejo package
registries are not a distribution target.

## Current status

Production publication remains blocked until the current Go `ts-deploy` CLI and
the protected `develop` to `main` process from the workspace `GOAL.md` are
wired and qualified. Until then, this document defines the checks and release
constraints, not an executable publish path.

Do not weaken a gate or use the old workflow as release authority while that
replacement is pending.

## Required checks

Run the complete repository set from a clean checkout:

```bash
npm install
npm run typecheck
npm run build
npm test
npm run check:openapi-drift && npm run check:route-manifest \
  && npm run check:forbidden-surface
npm run test:browser
```

Release automation must also pack the package once, install that exact tarball
in a clean consumer, inspect its public exports, scan it for credentials, and
run the package against the pinned Node.js 22 and 24 binaries. The browser lane
uses pinned current Chromium, Firefox, and WebKit engines. A skipped or
host-blocked required lane blocks publication.

The route manifest and OpenAPI descriptor must match the owner-published
contracts. Dashboard owns identity and product routes. The Go Database API in
`ts-internals` owns database and reactive routes. A missing contract stays
missing from the SDK.

The reactive gate checks the generated `terrascale.sync.v2.msgpack` manifest,
hostile decoding, golden bytes, and result-view behavior. MessagePack carries
the envelope and operation structure. Document bodies remain raw
TerraScale-canonical JSON bytes, and the client performs one native
`JSON.parse` per body after decoding the envelope. WebSocket is the fast path,
HTTP + MessagePack is the fallback, and there is no JSON WebSocket variant.

The local SQLite runtime cannot qualify live API behavior, authorization,
multi-Region durability, recovery, billing, or performance. Live integration
configuration that is absent or invalid is a failed release prerequisite, not
a passing result.

## Protected release

Agents commit green changes directly to `develop`. There are no pull requests.
The owner promotes `develop` to `main` only after the required checks pass.
That protected `main` run must:

1. build one immutable npm tarball from the promoted commit;
2. bind the tarball to the exact commit, dependency lock, generated contracts,
   Node binaries, npm version, and browser engines;
3. verify that the package version is not already present on npm;
4. run the release-only live checks without exposing their credentials to
   ordinary branch jobs; and
5. publish the already verified tarball with `--access public`.

The publish job receives only the npm credential and live-test secrets it
needs. It removes inherited npm configuration and does not expose credentials
to package inspection or browser bundles.

If npm publication is interrupted or returns an uncertain result, inspect the
public registry before retrying. npm versions are immutable. Never overwrite a
published version or rebuild different bytes under the same version.

## Versioning

Use a patch version for backward-compatible changes. Before `1.0`, a
source-breaking exported TypeScript change requires a minor version. Update
`package.json` and `package-lock.json` together before promotion.
