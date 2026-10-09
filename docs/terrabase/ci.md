# CI and releases

Forgejo Actions on the `terrascale-builder` runner (ts-builder image, .NET SDK 11 RC1).

| Workflow | When | Jobs | Blocks merges/releases |
| --- | --- | --- | --- |
| `fast.yml` | push to `main` or of a `v*` tag, pull requests, manual | `fast` and `release` in parallel, then `publish` (pushes only), then `deploy` (`main` only) | yes |
| `slow.yml` | push to `main` (beside `fast.yml`), nightly 03:17 UTC, manual | `slow` | never |

- **fast**: `make restore` (locked mode), `make fmt-check`, `make build CONFIGURATION=Release`,
  `make test-fast CONFIGURATION=Release`.
- **release**: `scripts/ci/stage-release.sh` runs `make publish` for `linux-x64` and
  `linux-arm64` and stages `terrabase-linux-amd64` and `terrabase-linux-arm64`. Both must be
  ELF64 little-endian executables for their machine (`e_machine` x86-64 and aarch64). The amd64
  bytes are smoke-run: `/healthz` must report the csproj version, `/readyz` must be ready, and
  an unknown setting must exit with 2. The x86_64 runner cannot run the arm64 bytes, so the ELF
  header check is all they get before release.
  On a push to `main` or of a `v*` tag, `scripts/ci/release.sh draft` attaches those bytes and
  their checksums (`terrabase-linux-amd64`, `terrabase-linux-arm64` and a `.sha256` for each,
  verifiable with `sha256sum -c`) to a draft release whose notes list both SHA-256 sums:
  - `main`: prerelease `ci-<sha>-run-<run id>`;
  - tag `v<Version>`: release `<tag>` with notes generated from the first-parent history since
    the previous `v*` tag. The tag must match `<Version>` in the csproj and point at a commit on
    `main`; a semantic-version prerelease tag (`v1.2.0-rc.1`) stays a prerelease.
- **publish**: runs only after `fast` and `release` both pass, and publishes that draft
  (`scripts/ci/release.sh publish`). A draft is visible only to repository writers and
  Forgejo creates its missing tag only on publish, so a release that failed a gate never
  appears. A rerun replaces a leftover draft; a published release is never replaced.
  `scripts/ci/check-release.sh` proves this on every run first: it drives `release.sh` against
  a fake release API in a scratch repository.
- **deploy** (TS-771): on every push to `main`, once `publish` passed,
  `scripts/ci/deploy.sh` hands the
  published `terrabase-linux-arm64` to ts-orchestrator: `ts-orch release service-deploy` (the
  ts-orchestrator CI build pinned by `TS_ORCH_RELEASE` and its `TS_ORCH_SHA256`) signs a release claim with the scoped release key it
  reads from OCI Vault, publishes it with the binary in GRU and SFO's head buckets in the
  `production` environment and waits
  until that region's orchestrator reports every member serving it. The orchestrator gates the
  rollout on member health and rolls a release that never serves back; a region that rolls back
  fails the job, and later regions never see the release. GRU and SFO are fixed in the
  script until TS-768 supplies discovery and independent regional rollouts through the
  shared CLI. Deployment has no opt-in switch. The job's
  inputs are the secrets `ORG_READ_TOKEN` and `TS_RELEASE_OCI_KEY` and the variables listed in
  `scripts/ci/deploy.sh`; the flow is documented in ts-orchestrator
  `docs/continuous-delivery.md`, "Automatic service releases". `scripts/ci/check-deploy.sh`
  runs the script against a stub API and ts-orch in `release`.
- **slow**: `scripts/ci/check-reporter.sh` first proves that the failure reporter redacts and
  stays within its memory bound, then `make test-slow CONFIGURATION=Release` runs. A failure opens the issue "Slow test suite
  failed", or comments on it if it is already open (`scripts/ci/report-slow-failure.sh`).
- **prune** (TS-608, `prune-releases.yml`, daily and on dispatch with `dry_run`): deletes
  the CI pre-releases past the newest 20 that are older than 7 days through the Forgejo API
  (their attachments leave the ts-forgejo bucket; tags stay), except releases a region pins:
  the `terrabase` digest each head bucket's `status.json` serves (`ts-orch release
  fleet-status --role terrabase`, run from `TS_PRUNE_ORCH_RELEASE`/`TS_PRUNE_ORCH_SHA256`, or
  else `TS_ORCH_RELEASE`/`TS_ORCH_SHA256`) and the variable `TS_RELEASE_PINS` (revisions,
  digests or tags, e.g. a rollback target). It logs each deletion and one
  `TS_RELEASE_PRUNE_V1` summary line. `scripts/ci/prune-releases.sh` is ts-orchestrator's
  script, tested there; ts-orchestrator `docs/continuous-delivery.md`, "CI pre-release
  retention", has the rules.

Branch protection on `main` should require `Fast integration / fast` and
`Fast integration / release`.

## Speed

- `fast` and `release` run in parallel; `publish` only publishes the draft and never rebuilds.
- NuGet downloads (`.nupkg`) persist in `/cache/nuget/terrabase/http-cache` on the runner.
  Jobs also share `/cache/nuget/terrabase/scratch` (`NUGET_SCRATCH`), so NuGet's filesystem
  locks coordinate concurrent HTTP-cache writes, including vulnerability-data updates.
  Each job extracts them into its own fresh packages folder, and locked restore checks every
  `.nupkg` against `packages.lock.json` (NU1403). A pull request therefore cannot plant files that a
  `main` release reuses (`scripts/ci/nuget-cache.sh`).
- `release` publishes only the server project (once per runtime); tests are built in `fast` alone.

## Requirements

- No secrets. `TerraScale.Endpoints` (ts-libs) restores as a package from the TerraScale
  Forgejo registry, which serves it without credentials; bump it in `Directory.Packages.props`.
- Releases use the job token (`forgejo.token`), which can write releases on pushes only; pull
  requests get a read-only job token.
