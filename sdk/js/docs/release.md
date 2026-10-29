# SDK release process

The release artifact is one immutable public npm tarball. Qualification installs
those exact bytes in a separate consumer; source tests alone cannot qualify it.
A version tag publishes it: see [Publishing](#publishing). Capabilities still
pending C06/C07 management and credential admission or native single-writer
security persistence fail at runtime with explicit capability errors.

The GitHub Actions workflow `.github/workflows/ci.yml` runs on GitHub-hosted
`ubuntu-24.04` runners. It uses Node 24.21.0 (22.23.2 for scheduled and manual
runs), provisions the reviewed npm CLI, and audits every committed npm graph.
Pull request and merge-queue runs that change non-documentation files install
the locked dependency tree, then typecheck, build type declarations, check the
v1 route manifest and run `npm test`. A push to `main` reuses a passing pull
request check for the identical tree. Browser, integration and real-service checks are not part of
GitHub CI. Frozen contract bytes or archived overlays are not runtime
qualification evidence.

Run the current local checks at low CPU priority:

```sh
nice -n 19 npm ci
nice -n 19 npm run typecheck
nice -n 19 npm run build
nice -n 19 npm run check:v1-route-manifest
nice -n 19 npm run check:forbidden-surface
nice -n 19 npm run check:sbom
nice -n 19 npm run check:vulnerabilities
nice -n 19 npm run provision:terrabase
nice -n 19 npm run test:integration
nice -n 19 npm run check:frameworks
nice -n 19 npm run test:browser
```

The integration runner requires the pinned binary acquired by the provisioner;
see [the README](../README.md#ouroboros-and-qualification). Missing real-service
inputs block required qualification. A build or a compatibility suite does not
establish deployed account flows or production security authority.

The Linux browser matrix uses actual stable Chrome and Edge channels and
Playwright Firefox and WebKit. CI provisions SHA-bound Nix vendor packages from
revision `b4fd65b198c599cbe814fcb9f42d25d021595ec9` (Chrome 154.0.8037.57 and
Edge 154.0.4258.37) and exports launchers for those executables. The launchers
clear inherited `LD_LIBRARY_PATH` and `LD_PRELOAD` only for vendor browser
processes, so their Nix runtime libraries stay coherent while other tools and
engines retain the builder environment. Local runs can set
`PLAYWRIGHT_CHROME_EXECUTABLE_PATH` and `PLAYWRIGHT_EDGE_EXECUTABLE_PATH` for
verified runner-provisioned binaries. Refresh these pins as stable releases move.
WebKit results are engine compatibility evidence; actual Safari qualification
requires a macOS runner.

Fake/local adapter fixtures are confined to test code and test binaries and
must never clear deployment indicators or become shipped application modes.
Maintained Better Auth suites verify local adapter
semantics; their results do not discharge production security gates. Pure packed
browser tests verify browser compatibility, while the separate real-allocation
browser journey requires actual service credentials and does not use intercepted
producer responses.

`qualify-npm-artifact.js` preserves clean-source, reviewed toolchain, installed
consumer, archive, audit, license, and cryptographic evidence checks. It
admits only the pinned runtime dependency `ulid@3.0.2` with its MIT license;
qualification binds the installed version and exact LICENSE SHA-256, rejects
unqualified transitive dependencies, and promotion verifies the same evidence.
Its report retains active producer blockers and cannot mark a release eligible while they
remain. `promote-npm-candidate.js` requires an admitted build receipt bound to
source and output bytes and refuses publication with active blockers. The
required release-E2E check fails before any skip-capable suite until production
persistence and deployed account flows are admitted. Environment variable
presence is not successful journey evidence.

Hosted OAuth resource conformance must bind the accepted producer protocol snapshot and its actual HTTP response artifact manifest. Request-only controls use deliberately failing transports; captured-response playback proves decoding only and is not hosted or native qualification. Active resource responses are checked against independently configured canonical issuer, exact registered audience, fresh local time, and user/service session shape. Keep no-resource browser login supported, resource Basic introspection separate from client-secret-post token and client introspection, and management authorization separate from token activity.

Client/resource management responses use exact snake_case records and safe numeric epochs. Verify both client PATCH branches (bare view without rotation, one-time secret envelope with rotation), the always-enveloped resource create/PATCH response, inactive responses, and actual 401/500 errors. Set `TERRASCALE_AUTH_COMPONENT_RESPONSES` to the accepted immutable captured manifest for component-byte decoding checks. An expired retained response must fail acceptance; do not rewrite its timestamps or claim a live authenticated exchange from playback.

## Publishing

`.github/workflows/publish.yml` publishes the `@terrascale/sdk` npm package when
a `v0.0.N` tag is pushed. The tag must match the `package.json` version, which
must match `^0\.0\.[0-9]+$`. The job audits the npm graph, runs the typecheck,
route-drift check and `npm test`, then runs `npm publish --provenance --access
public`. Bump the version to release. The workflow uses npm trusted publishing
(OIDC) when configured and otherwise reads `NPM_TOKEN` as an Actions secret.
The TS-795 operating contract requires vendor-issued publication credentials
to be stored once in OCI Vault and delivered to the job automatically, with
first-party secrets created/delivered automatically. No hand-copying step is
part of onboarding. This docs change does not implement or verify that delivery
integration; missing credentials must continue to fail closed. Never publish
secret values or private storage paths.
