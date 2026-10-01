# SDK release process

The release artifact is one immutable public npm tarball. Qualification installs
those exact bytes in a separate consumer; source tests alone cannot qualify it.
CI publishes automatically: see [Publishing](#publishing). Capabilities still
pending C06/C07 management and credential admission or native single-writer
security persistence fail at runtime with explicit capability errors.

The native Forgejo workflow verifies the event revision on Node 22.23.2 and
24.21.0 and current browser engines. All jobs pin the existing builder image to
`sha256:265dd0079910888f668752f27341065df6228eaa2306fd467b424cd042fe1f60`,
with the runner label selecting capacity. It provisions the reviewed npm 11.18.0
CLI, uses the locked dependency tree, and runs source parity, typecheck, build,
regression, actual framework lifecycle, SBOM, and vulnerability checks. Exact
producer OpenAPI and canonical vector bytes are mandatory inputs checked against
`sdk-current-contract.json`. CI verifies and extracts the immutable
`scripts/ci/producer-contract-inputs.tar.gz` archive (SHA-256
`8169fab845e3130b54378ff9a064ff212d1ff59fe15eb82c857718ad919db7e7`).
Its `producer-inputs-v1.json` and original frozen manifests bind independently
consumed actual producer files. They are an uncommitted overlay on producer base
`470d1f898897d4a0cefc7d638ce8eb72f0ca73b8`; the base alone does not reproduce
them. These inputs establish source parity, with producer runtime qualification
still pending.

```sh
npm ci
npm run check:current-contract-inputs
npm run check:current-contract
npm run typecheck
npm run build
npm test
npm run check:frameworks
npm run check:route-manifest
npm run check:openapi-contract-descriptor
npm run check:forbidden-surface
npm run check:sbom
npm run check:vulnerabilities
npm run test:browser
npm run qualify:current-producer
npm run qualify:auth-authority
npm run qualify:release-e2e
```

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

Local SQLite tests run only in permitted development environments and must never
clear deployment indicators. Maintained Better Auth suites verify local adapter
semantics; their results do not discharge production security gates. Pure packed
browser tests verify browser compatibility, while the separate real-allocation
browser journey requires actual service credentials and does not use intercepted
producer responses.

`qualify-npm-artifact.js` preserves clean-source, reviewed toolchain, installed
consumer, archive, audit, license, and cryptographic evidence checks. Its report
retains active producer blockers and cannot mark a release eligible while they
remain. `promote-npm-candidate.js` requires an admitted build receipt bound to
source and output bytes and refuses publication with active blockers. The
required release-E2E check fails before any skip-capable suite until production
persistence and deployed account flows are admitted. Environment variable
presence is not successful journey evidence.

Hosted OAuth resource conformance must bind the accepted producer protocol snapshot and its actual HTTP response artifact manifest. Request-only controls use deliberately failing transports; captured-response playback proves decoding only and is not hosted or native qualification. Active resource responses are checked against independently configured canonical issuer, exact registered audience, fresh local time, and user/service session shape. Keep no-resource browser login supported, resource Basic introspection separate from client-secret-post token and client introspection, and management authorization separate from token activity.

Client/resource management responses use exact snake_case records and safe numeric epochs. Verify both client PATCH branches (bare view without rotation, one-time secret envelope with rotation), the always-enveloped resource create/PATCH response, inactive responses, and actual 401/500 errors. Set `TERRASCALE_AUTH_COMPONENT_RESPONSES` to the accepted immutable captured manifest for component-byte decoding checks. An expired retained response must fail acceptance; do not rewrite its timestamps or claim a live authenticated exchange from playback.

## Publishing

The `release/npm` job in `.forgejo/workflows/ci.yml` publishes the `terrascale`
npm package on every push to `main`, after the static, Node and browser jobs
pass. It skips immediately when npm already has the `package.json` version;
otherwise it packs the tarball once and publishes those exact bytes. Bump the
version to release. The publish token is the Forgejo action secret `NPM_TOKEN`;
its canonical copy is the operator-only OpenBao object
`/infra/npm/terrascale-publish` (`NPM_TOKEN`, `NPM_USER`).
