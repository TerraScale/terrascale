# CI and releases

Forgejo Actions on the `terrascale-builder` runner (ts-builder image, .NET SDK 11 RC1).

| Workflow | When | Jobs | Blocks merges/releases |
| --- | --- | --- | --- |
| `ci.yml` | push to `main` or of a `v*` tag, pull requests, manual | `guard` and `check` (display name `fast`), then `release`, then `publish` (pushes only), then `deploy` (`main` only) | yes |
| `slow.yml` | nightly 03:17 UTC, manual | `slow` | never |

- **check** (display name `fast`, the required status): one `make check CONFIGURATION=Release` invocation shares locked restore
  and build prerequisites across the formatting, build, analyzer regression and Fast gates.
  Formatting checks whitespace; the build enforces style and analyzers as errors.
- **release**: `scripts/terrabase/ci/stage-release.sh` runs `make publish` for `linux-x64` and
  `linux-arm64` and stages `terrabase-linux-amd64` and `terrabase-linux-arm64`. Both must be
  ELF64 little-endian executables for their machine (`e_machine` x86-64 and aarch64). The amd64
  bytes are smoke-run: `/healthz` must report the csproj version, `/readyz` must be ready, and
  an unknown setting must exit with 2. The x86_64 runner cannot run the arm64 bytes, so the ELF
  header check is all they get before release.
  On a push to `main` or of a `v*` tag, `scripts/terrabase/ci/release.sh draft` attaches those bytes and
  their checksums (`terrabase-linux-amd64`, `terrabase-linux-arm64` and a `.sha256` for each,
  verifiable with `sha256sum -c`) to a draft release whose notes list both SHA-256 sums:
  - `main`: prerelease `ci-<sha>-run-<run id>`;
  - tag `v<Version>`: release `<tag>` with notes generated from the first-parent history since
    the previous `v*` tag. The tag must match `<Version>` in the csproj and point at a commit on
    `main`; a semantic-version prerelease tag (`v1.2.0-rc.1`) stays a prerelease.
- **publish**: runs only after `fast` and `release` both pass, and publishes that draft
  (`scripts/terrabase/ci/release.sh publish`). A draft is visible only to repository writers and
  Forgejo creates its missing tag only on publish, so a release that failed a gate never
  appears. A rerun replaces a leftover draft; a published release is never replaced.
  `scripts/terrabase/ci/check-release.sh` proves this on every run first: it drives `release.sh` against
  a fake release API in a scratch repository.
- **deploy** (TS-771): on every push to `main`, once `publish` passed,
  `scripts/terrabase/ci/deploy.sh` hands the
  published `terrabase-linux-arm64` to ts-orchestrator: `ts-orch release service-deploy` (the
  ts-orchestrator CI build pinned in the workflow by `TS_ORCH_RELEASE` and its `TS_ORCH_SHA256`) signs a release claim with the scoped release key it
  reads from OCI Vault. Serving regions come from orch's published topology;
  each region's admitted environment comes from its fresh controller report.
  Regions sign, publish and wait in parallel, polling every five seconds with
  the pinned helper's deadline and no caller timing override. The retained helper
  defaults to 30 minutes per region; TS-783 helpers use a fixed 20-minute budget
  across regions. The orchestrator gates each rollout on member health
  and rolls a release that never serves back. Every region finishes independently;
  a failure in any region fails the job after the others finish. No repository
  region list, fixed environment or opt-in switch remains. The job's
  inputs are the secrets `ORG_READ_TOKEN` and `TS_RELEASE_OCI_KEY` and the variables listed in
  `scripts/terrabase/ci/deploy.sh`; the flow is documented in ts-orchestrator
  `docs/continuous-delivery.md`, "Automatic service releases". `scripts/terrabase/ci/check-deploy.sh`
  runs the script against a stub API and ts-orch in `release`.
- **slow**: `scripts/terrabase/ci/check-reporter.sh` first proves that the failure reporter redacts and
  stays within its memory bound, then `make test-slow CONFIGURATION=Release` runs. A failure opens the issue "Slow test suite
  failed", or comments on it if it is already open (`scripts/terrabase/ci/report-slow-failure.sh`).
  The real ts-auth case acquires immutable AMD64 executables automatically. Auth uses its
  qualified private storage dependency; this checkout is the authentication target. The
  prune workflow retains that storage release. CI uses its job token; local runs use the
  existing `fj` login. Acquisition needs curl, Python3, tar and `fj` on Linux/AMD64.
- **prune** (TS-608, `prune-releases.yml`, daily and on dispatch): deletes
  the CI pre-releases past the newest 20 that are older than 7 days through the Forgejo API
  (their attachments leave the ts-forgejo bucket; tags stay), except releases a region pins:
  the `terrabase` digest each head bucket's `status.json` serves (`ts-orch release
  fleet-status --role terrabase`, run from `TS_PRUNE_ORCH_RELEASE`/`TS_PRUNE_ORCH_SHA256`, or
  else `TS_ORCH_RELEASE`/`TS_ORCH_SHA256`) and the variable `TS_RELEASE_PINS` (revisions,
  digests or tags, e.g. a rollback target). It logs each deletion and one
  `TS_RELEASE_PRUNE_V1` summary line. `scripts/terrabase/ci/prune-releases.sh` follows ts-orchestrator's
  retention rules and always executes; `scripts/terrabase/ci/check-prune.sh` verifies deletion,
  protected releases and refusal without live pins against an isolated HTTP API.
  ts-orchestrator `docs/continuous-delivery.md`, "CI pre-release retention", has the rules.

Branch protection on `main` should require `Fast integration / fast` and
`Fast integration / release`.

## Speed

- `guard` and `check` run first; `release` starts after both; `publish` only publishes the draft and never rebuilds.
- `fast` restores once and builds once. `dotnet format whitespace` checks formatting
  without repeating the style/analyzer work performed by the warnings-as-errors build.
- Test processes do not inherit the GitHub artifact-upload credentials. TUnit 1.19.57's
  automatic upload fails against Forgejo's API after the tests finish, adding 77–80 seconds
  of retries to observed runs. Console summaries and failure logs remain enabled.
- HTML and JSON test reporting always run. TUnit 1.73.19 includes upstream PR #5352,
  which caps external spans at 100 per test, with a per-trace fallback when remote HTTP
  ancestry is missing. This repairs the uncapped infrastructure-span retention in 1.19.57.
  Framework spans and individual tag payloads are not globally byte-bounded; the full
  parallel suites validate this workload without a reporter off-switch.
- Pruning always executes its retention policy, and Slow failures always file an issue.
  Their checks use isolated HTTP/CLI fakes in test scripts; neither production script has
  a dry-run or opt-out path. Live release pins and unreadable manifests remain protected.
- NuGet downloads (`.nupkg`) persist in `/cache/nuget/terrabase/http-cache` on the runner.
  Jobs also share `/cache/nuget/terrabase/scratch` (`NUGET_SCRATCH`), so NuGet's filesystem
  locks coordinate concurrent HTTP-cache writes, including vulnerability-data updates.
  Each job extracts them into its own fresh packages folder, and locked restore checks every
  `.nupkg` against `packages.lock.json` (NU1403). A pull request therefore cannot plant files that a
  `main` release reuses (`scripts/terrabase/ci/nuget-cache.sh`).
- `release` publishes only the server project (once per runtime); tests are built in `fast` alone.
- Slow runs nightly and on demand, so each main push does not enqueue another expensive suite.
  Its failure reporter remains active; Fast still gates every merge, publication and deployment.

## Requirements

- No secrets. `TerraScale.Endpoints` (this repo) restores as a package from the TerraScale
  Forgejo registry, which serves it without credentials; bump it in `Directory.Packages.props`.
- Releases use the job token (`forgejo.token`), which can write releases on pushes only; pull
  requests get a read-only job token.
