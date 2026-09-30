# SDK release process

The release artifact is one immutable public npm tarball. Qualification installs
those exact bytes in a separate consumer; source tests alone cannot qualify it.
Production publication remains blocked by C06/C07 management and credential
admission, native single-writer security persistence, and installed real-producer
and deployed authenticated account/browser journeys.

The native Forgejo workflow verifies the event revision on Node 22.23.2 and
24.21.0 and current browser engines. It provisions the reviewed npm 11.18.0 CLI,
uses the locked dependency tree, and runs source parity, typecheck, build,
regression, actual framework lifecycle, SBOM, and vulnerability checks. Exact
producer OpenAPI and canonical vector bytes are mandatory inputs checked against
`sdk-current-contract.json`. The final accepted producer revision or immutable
artifact must be provisioned before source gates can complete in CI; historical
contract archives cannot satisfy this requirement.

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
Playwright Firefox and WebKit. Set `PLAYWRIGHT_CHROME_EXECUTABLE_PATH` and
`PLAYWRIGHT_EDGE_EXECUTABLE_PATH` for verified runner-provisioned binaries.
WebKit results are engine compatibility evidence; actual Safari qualification
requires a macOS runner.

Local SQLite tests run only in permitted development environments and must never
clear deployment indicators. Maintained Better Auth suites verify local adapter
semantics; their results do not discharge production security gates. Pure packed
browser tests verify browser compatibility, while the separate real-allocation
browser journey requires actual service credentials and does not use intercepted
producer responses.

`qualify-npm-artifact.ts` preserves clean-source, reviewed toolchain, installed
consumer, archive, audit, license, and cryptographic evidence checks. Its report
retains active producer blockers and cannot mark a release eligible while they
remain. `promote-npm-candidate.ts` requires an admitted build receipt bound to
source and output bytes and refuses publication with active blockers. The
required release-E2E check fails before any skip-capable suite until production
persistence and deployed account flows are admitted. Environment variable
presence is not successful journey evidence.

Hosted OAuth resource conformance must bind the accepted producer protocol snapshot and its actual HTTP response artifact manifest. Request-only controls use deliberately failing transports; captured-response playback proves decoding only and is not hosted or native qualification. Active resource responses are checked against independently configured canonical issuer, exact registered audience, fresh local time, and user/service session shape. Keep no-resource browser login supported, resource Basic introspection separate from client-secret-post token and client introspection, and management authorization separate from token activity.

Client/resource management responses use exact snake_case records and safe numeric epochs. Verify both client PATCH branches (bare view without rotation, one-time secret envelope with rotation), the always-enveloped resource create/PATCH response, inactive responses, and actual 401/500 errors. Set `TERRASCALE_AUTH_COMPONENT_RESPONSES` to the accepted immutable captured manifest for component-byte decoding checks. An expired retained response must fail acceptance; do not rewrite its timestamps or claim a live authenticated exchange from playback.
