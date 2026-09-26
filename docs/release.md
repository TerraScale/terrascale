# SDK release process

`@terrascale/sdk` publishes to the public npm registry. Forgejo package
registries are not a distribution target.

## Current status

Native CI runs on pushes to `develop` and `main` and on pull requests to
`main`. The static contract gates, the Node.js `22.23.2` and `24.21.0` matrix,
and the browser lane run on `terrascale-builder`. Its image includes Node.js
`24.21.0` and the pinned Playwright Chromium and Firefox engines; the browser
lane installs WebKit. Jobs check out the event revision, and the static and
Node jobs also check out the pinned TerraBase (`TerraScale/ts-rust`) and edge
(`TerraScale/ts-atlas`) contract bundles. Checkouts use `REGISTRY_TOKEN` with
`persist-credentials: false`; jobs do not receive npm, release, or live API
secrets.

Production publication remains blocked. The unchanged
`scripts/promote-npm-candidate.ts` verifier requires a real reviewed
`ts-deploy` build receipt bound to the source tree, output files, checksums,
and qualification evidence, and native CI currently has no accepted producer
for that receipt. The native workflow therefore has no candidate, admission,
or publish path.

Do not weaken the verifier or publish npm bytes while that producer contract is
pending.

## Required checks

Run the complete repository set from a clean checkout:

```bash
npm ci
npm run typecheck
npm run build
npm test
npm run check:public-contract-fixture
npm run check:openapi-contract-descriptor && npm run check:route-manifest \
  && npm run check:forbidden-surface
npm run check:sbom && npm run check:vulnerabilities
npm run test:browser
```

Release automation must also pack the package once, install that exact tarball
in a clean consumer, inspect its public exports, scan it for credentials, and
run the package against the pinned Node.js 22 and 24 binaries. The browser lane
uses pinned current Chromium, Firefox, and WebKit engines. A skipped or
host-blocked required lane blocks publication.

The route manifest and OpenAPI descriptor must match the owner-published
TerraBase and edge contract bundles selected by `CONTRACT_SOURCE` or
`CONTRACT_URL` and `EDGE_CONTRACT_SOURCE` or `EDGE_CONTRACT_URL`. A missing
contract stays missing from the SDK.

The reactive gate checks the generated `terrascale.sync.v1.msgpack` manifest,
hostile decoding, golden bytes, and result-view behavior. MessagePack carries
the envelope and operation structure. Document bodies remain raw
TerraScale-canonical JSON bytes, and the client performs one native
`JSON.parse` per body after decoding the envelope. HTTP is the default
transport, WebSocket and server-sent events are opt-in upgrades, and there is
no JSON WebSocket variant.

The local SQLite runtime cannot qualify live API behavior, authorization,
multi-Region durability, recovery, billing, or performance. Live integration
configuration that is absent or invalid is a failed release prerequisite, not
a passing result.

## Protected release when publication is restored

Agents commit green changes directly to `develop`; pull requests to `main` run
the same native CI. The owner may restore a main-only release graph after
accepting a native receipt producer and promoting `develop` to `main`. That
protected `main` run must:

1. build one immutable npm tarball from the promoted commit;
2. bind the tarball to the exact commit, dependency lock, generated contracts,
   Node binaries, npm version, and browser engines;
3. verify that the package version is not already present on npm;
4. run the release-only live checks without exposing their credentials to
   ordinary branch jobs; and
5. publish the already verified tarball with `--access public`.

The final publish job receives only the npm credential. Live-test secrets remain
scoped to their verification job, and the publish job removes inherited npm
configuration before invoking the unchanged promotion verifier.

If npm publication is interrupted or returns an uncertain result, inspect the
public registry before retrying. npm versions are immutable. Never overwrite a
published version or rebuild different bytes under the same version.

## Versioning

Use a patch version for backward-compatible changes. Before `1.0`, a
source-breaking exported TypeScript change requires a minor version. Update
`package.json` and `package-lock.json` together before promotion.

## Dependency SBOM

The SDK CI runs `npm run check:sbom`, which generates a CycloneDX 1.5 inventory
from the committed lockfile, verifies the package identity and every npm
component reference, rejects duplicate or malformed component identities, and
records the SHA-256 digest of `package-lock.json` in the evidence envelope. A
release process must retain that generated envelope with the candidate receipt
and complete vulnerability review before publication.

For consumers that download the signed edge archive, set both
`EDGE_CONTRACT_URL` and `EDGE_CONTRACT_ARCHIVE_SHA256`; the SDK refuses an edge
URL without its immutable digest or when a local source and URL are supplied
simultaneously.
