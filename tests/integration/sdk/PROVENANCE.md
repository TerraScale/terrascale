# Pinned SDK test snapshot

This directory vendors the first-party TerraScale TypeScript SDK source only
for the dashboard integration test. It preserves the SDK's original source,
package metadata, lockfile, build configuration, public route manifest, README,
and license. The Go application and production binary do not depend on this
snapshot.

## Source identity

- Repository: `TerraScale/ts-typescript`
- Commit: `d1021878d0d0dfeb5639ea3dd0f1ed7f0a9eee19`
- Commit tree: `e9c5098964f9b8f5a3d0f3cb9a3239dbe2b5b3f5`
- Package version: `@terrascale/sdk@0.3.0`
- The sibling checkout was at that commit with a clean worktree and index when
  the snapshot was made.
- `PINNED_FILES.sha256` records SHA-256 hashes for every file copied from that
  commit.
- The pinned snapshot consists of 117 files totaling 1,684,198 bytes: 110 files
  under `src/` and the seven listed upstream package/config/license files.
  Those source files include 51 `*.test.ts` files because the upstream build
  compiles all of `src/**/*.ts`.

## Included files

- The complete `src/` tree. The upstream build configuration compiles all of
  `src/**/*.ts`, including source-local tests and integration fixtures, so
  those files are required by the unmodified build command.
- `package.json`, `package-lock.json`, `tsconfig.json`, and
  `tsconfig.build.json` from the pinned commit.
- `sdk-route-manifest.json`, which the published package includes.
- The pinned package `README.md` and first-party `LICENSE`.

Root-level tests, browser tests, scripts, docs, CI files, `AGENTS.md`, and Git
metadata are omitted. The pinned repository has no root `NOTICE` file; no
license or notice was invented.

## Reproduce the test package

From the dashboard repository root, with Node 22 or 24 and npm 11:

```sh
cd tests/integration/sdk
npm ci
npm run build
```

The integration harness imports `tests/integration/sdk/dist/index.js`, the
upstream package's root entrypoint. `dist/` and `node_modules/` are generated
locally and ignored by this directory's `.gitignore`.

Packaging verification on Node v24.21.0 and npm 11.19.0:

- `npm ci` installed the locked 74 packages from public npm registry URLs and
  reported zero vulnerabilities.
- `npm run build` succeeded twice. SHA-256 of the sorted per-file `dist/`
  checksum listing was
  `278843110a17c3f1a8adf2139330e2dfb73f6bb836460ef1b9376106a6e2fcf6` on both
  builds.
- Importing `./dist/index.js` exposed the original `createTerraScaleClient`
  function.
