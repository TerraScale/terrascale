# Deploy matrix

How each deployable monorepo project goes from a main push to production. Verified 2026-11-06 at the
commit that added this file. `scripts/ci/path-filter-test.py` (run by the CI `checks` job) pins the trigger routing below and fails on any dead path glob.

Every gate is a GitHub Actions job in the same run: each affected area builds and runs its Fast suite on a hosted
runner before anything deploys; no gate reads commit statuses. Production runs linux/arm64 only, so each service
release job publishes only that runtime (`scripts/ci/image-payloads.sh publish`) and every image is built for that
platform alone. Release jobs run on `ubuntu-latest` (x64; the free arm64 runner builds slower) and restore only their own
project, locked, from nuget.org: no GHA package cache. TerraBase publishes ReadyToRun per assembly, not as SDK 11's
default framework composite (see Release timing). Each
`ts-orch release app` runs through `scripts/ci/release-app.sh`: a heartbeat while ts-orch works, a hard limit (TerraBase
240 s, ts-auth and dashboard 180 s), then NATS reachability and public health on failure.

| Project | Trigger paths (main push) | Workflow chain | Gate status context | Deploy target | How verified locally |
| --- | --- | --- | --- | --- | --- |
| TerraBase | `ci.yml` runs on every push; its `terrabase` area: `src/TerraBase*`, `src/TerraScale.TerraBase.*`, `tests/TerraBase*`, `scripts/terrabase/**`, `scripts/ci/terrabase-*`, `terrabase.slnf`, `src/vendor/**`, the vendored driver tests, `scripts/vendor/**`, `sdk/dotnet/**`, library sources and shared build files | `ci.yml`: `changes` → `terrabase` (build, checks, Fast) → `test` (also requires `checks`, which runs beside `changes`: legacy scan, path-filter fixtures, workflow self-checks) → `main` (arm64 publish, arm64 image, `ts-orch release app`; push runs skip the release until the ZDT window's cienable) | `terrabase` job (`test-area.sh fast terrabase`) | `ghcr.io/terrascale/terrabase:<sha>`; OCI regions through ts-orch; `api.terrascale.tech` | actionlint; routing fixtures; arm64 payload and runtime image built (see Builds) |
| ts-auth | `ci.yml` `auth` area: `src/TerraScale.Authentication*`, `tests/TerraScale.Authentication*`, `scripts/auth/**`, `docs/auth/*.yaml`, `auth.slnf`, `scripts/ci/auth-build.sh`, `sdk/dotnet/**`, library sources and shared build files; TerraBase/vendor changes (`auth-server`) re-verify auth Fast | `ci.yml`: `auth` (build, checks, Fast) → `test` → `main-auth` (arm64 publish, arm64 image, release), in parallel with `main`; `main-auth-ordered` instead, after `main`, when one push changes both `auth-server` (TerraBase's server) and `auth-client` (`src/TerraScale.Authentication*`, `sdk/dotnet`), and on dispatches | `auth` job (`auth-build.sh`, `make auth-test-fast`) | `ghcr.io/terrascale/ts-auth:<sha>`; OCI regions through ts-orch; `auth.terrascale.tech` | actionlint; routing fixtures; `make auth-publish` arm64 payload and runtime image built (see Builds) |
| Dashboard | `images-ghcr.yml`: `src/TerraScale.Dashboard*`, `tests/TerraScale.Dashboard*`, `scripts/dashboard/**`, `src/TerraScale.TerraBase.Contracts/**`, `src/TerraScale.Endpoints/**`, `src/TerraScale.Endpoints.Generators/**`, `sdk/dotnet/**`, shared build files, `dashboard-ci.yml`, `images-ghcr.yml` | `images-ghcr.yml`: `ready` (`release-gate.py`: newest not-failed main commit with its own Images run; never waits for CI) ∥ `test` (`dashboard-ci.yml`) → `image` (download the `dashboard-publish` artifact Dashboard Fast built, arm64 image, gate again, `ts-orch release app`, `verify-live.py`); `workflow_run` after CI heals missed runs | `test` job (`make dashboard-test-fast`), no failed CI run for the commit | `ghcr.io/terrascale/terrascale-dashboard:<sha>`; OCI regions through ts-orch; `dash.terrascale.tech` | actionlint; release-gate tests; `dotnet publish` + `Dockerfile.runtime` image built (see Builds) |
| Docs site | `docs-ci.yml`: `docs-site/**`, `Makefile`, `docs-*.yml` | `docs-ci.yml`: `fast` → `deploy` (`docs-deploy.yml`: `eligible` → `deploy`, `npm run build`, `cf deploy --prebuilt`, `check-hosted.mjs`) | `fast` job (`make docs-test-fast`); dispatch also `check-main.py` | Cloudflare Workers; `docs.terrascale.tech` | actionlint; `check-main.test.py`; `npm ci && npm run build` (see Builds) |
| Landing site | `landing-ci.yml`, `landing-secret-scan.yml`: `landing-site/**`, `landing-*.yml` | `landing-ci.yml`: `test` (legacy scan, `npm ci`, `npm run build`, upload `landing-build`) → `deploy` (`npx wrangler deploy` of that artifact, live `build-info.json` revision) | `test` job | Cloudflare Worker `ts-landing`; `terrascale.tech`, `www.terrascale.tech` | actionlint; `npm ci && npm run build` (see Builds) |
| .NET SDK | Fast: `sdk-dotnet-ci.yml` (`sdk/dotnet/**`, `sdk-dotnet-*.yml`, `sdk-fast.yml`, `scripts/sdk/**`); release: tag `sdk/dotnet/vX.Y.Z` | `sdk-dotnet-release.yml`: `tests` (`sdk-fast.yml`) → `package` (`build-release.py dotnet`) → `github-packages` → `restore` → `nuget-org` | `sdk-fast.yml` (`make sdk-dotnet-test-fast`) | GitHub Packages, then nuget.org (go-live) | actionlint; `qualify-release.sh` packaging (see Builds) |
| JS SDK | Fast: `sdk-js-ci.yml`; release: tag `sdk/js/v0.0.*` | `sdk-js-release.yml`: `tests` → `publish` (`build-release.py js`, `npm publish --provenance`) | `sdk-fast.yml` (`make sdk-js-test-fast`) | npm `@terrascale/sdk` (go-live) | actionlint; `qualify-release.sh` packaging (see Builds) |
| Python SDK | Fast: `sdk-python-ci.yml`; release: tag `sdk/python/v0.0.*` | `sdk-python-release.yml`: `tests` → `publish` (environment `pypi`, trusted publishing) | `sdk-fast.yml` (`make sdk-python-test-fast`) | PyPI (go-live) | actionlint; `qualify-release.sh` packaging (see Builds) |
| Rust SDK | Fast: `sdk-rust-ci.yml`; release: tag `sdk/rust/v0.0.*` at main head | `sdk-rust-release.yml`: `tests` → `publish` (`build-release.py rust`, `cargo publish --no-verify`) | `sdk-fast.yml` (`make sdk-rust-test-fast`) | crates.io (go-live) | actionlint; `qualify-release.sh` packaging (see Builds) |
| Go SDK and CLI | Fast: `sdk-go-ci.yml`; release: `sdk-go-release.yml` on `sdk/go/**`, `scripts/sdk/**`, `sdk-go-release.yml`, `sdk-go-publish.yml` | `sdk-go-release.yml`: `tests` (newest green main) → `autorelease` (`build-release.py go`, create `sdk/go/v<version>`) → `publish` (calls `sdk-go-publish.yml`: GitHub release with archives, `checksums.txt`, `ts.rb`, `install.sh`) | `sdk-fast.yml` (`make sdk-go-test-fast`), green main | GitHub release `sdk/go/v0.0.X`; Go module tag; homebrew-tap syncs `ts.rb` | actionlint; reusable call inputs checked; `qualify-release.sh` packaging with GoReleaser snapshot (see Builds) |

Every other main push that selects no area runs only the CI `changes` and `checks` jobs: `docs/` Markdown,
`aspire/`, `ops/`, `benchmark/` outside the two library benchmarks, `.cos/`, instruction files and
root files outside the shared build set.

## Local verification

- `actionlint` 1.7.12 with shellcheck 0.11.0 over every workflow: clean.
- `scripts/ci/path-filter-test.py` with the pinned dorny matcher: 68 routing fixtures and the dead-glob guard pass.
- Workflow reference audit (reusable `uses:` paths, inputs and secrets, run scripts, Makefile targets, npm scripts,
  working directories, `node-version-file`, `cache-dependency-path`, artifact producer/consumer names): no mismatches.

## Builds

Each deploy artifact CI builds, built on mariogk-dev through `ts-heavy-any` from main `d79589c6b` (the
base of this change; none of its workflow commits alters a build input), with the SDK fixes of this
change applied on top for the SDK rows. Nothing was published.

| Artifact | Command | Exit |
| --- | --- | --- |
| TerraBase payload | `make terrabase-publish RUNTIME=linux-arm64` (283 MB; 441 s cold in a 3-CPU dev-host slot; 198 MB since per-assembly ReadyToRun, 2026-11-07) | 0 |
| TerraBase image | runtime Dockerfile from `image-payloads.sh runtime-dockerfile terrabase`, `docker build --platform linux/arm64` (linux/arm64, 312 MB) | 0 |
| ts-auth payload | `make auth-publish RUNTIME=linux-arm64` (150 MB; 116 s cold in a 3-CPU dev-host slot) | 0 |
| ts-auth image | `docker build --platform linux/arm64` of the runtime Dockerfile (linux/arm64, 172 MB) | 0 |
| Dashboard | `dotnet publish src/TerraScale.Dashboard -c Release`; `docker build --platform linux/arm64 -f scripts/dashboard/ci/Dockerfile.runtime` (linux/arm64, 164 MB) | 0 |
| Docs site | `npm ci && npm run build` in `docs-site/` | 0 |
| Landing site | `npm ci && npm run build` in `landing-site/` | 0 |
| .NET SDK | `sdk/dotnet/scripts/qualify-release.sh`: release checks, two reproducible packs, consumer restore and audit | 0 (was 1 on main: NU1008, whitespace, non-reproducible entry timestamps) |
| Go SDK and CLI | `sdk/go/scripts/qualify-release.sh` (GoReleaser snapshot: 6 archives, `checksums.txt`, `ts.rb`, `install.sh`) | 0 |
| JS SDK | `sdk/js/scripts/qualify-release.sh` (`terrascale-sdk-0.0.19.tgz`) | 0 |
| Python SDK | `sdk/python/scripts/qualify-release.sh` (wheel and sdist 0.0.5) | 0 |
| Rust SDK | `sdk/rust/scripts/qualify-release.sh` (fmt, tests, clippy, publish dry run, audit, `terrascale-0.0.5.crate`) | 0 (was 1 on main: rustfmt, missing `.crate`) |

Each SDK's outputs match the files its release workflow builds and publishes.

## Release timing

Push to image pushed (the `main`/`main-auth` build-push step) on real main push runs; TerraBase's ts-orch release is
still held on push runs. Details and per-runner publish measurements: `benchmark/bench/2026-11-07-terrabase-ci-publish.md`.

| Run | Commit | TerraBase publish | TerraBase image | ts-auth image | ts-auth released |
| --- | --- | ---: | ---: | ---: | ---: |
| 42418535998 (before) | `7f43011fa` | 233 s | 371 s | 174 s | 228 s |
| 42425979354 (after) | `8741019f2` | 76 s | 140 s | 151 s | 204 s |
| 42426957542 (after) | `3d23b5484` | 115 s | 186 s | 120 s | 169 s |

`ubuntu-latest` lands on CPUs about 1.7x apart; the TerraBase compile, about half of it analyzers, is most of the
remaining spread.
