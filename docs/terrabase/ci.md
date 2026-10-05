# CI and releases

Forgejo Actions on the `terrascale-builder` runner (ts-builder image, .NET SDK 11 RC1).

| Workflow | When | Jobs | Blocks merges/releases |
| --- | --- | --- | --- |
| `fast.yml` | push to `main` or of a `v*` tag, pull requests, manual | `fast` and `release` in parallel, then `publish` (pushes only) | yes |
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
- **slow**: `scripts/ci/check-reporter.sh` first proves that the failure reporter redacts and
  stays within its memory bound, then `make test-slow CONFIGURATION=Release` runs. A failure opens the issue "Slow test suite
  failed", or comments on it if it is already open (`scripts/ci/report-slow-failure.sh`).

Branch protection on `main` should require `Fast integration / fast` and
`Fast integration / release`.

## Speed

- `fast` and `release` run in parallel; `publish` only publishes the draft and never rebuilds.
- NuGet downloads (`.nupkg`) persist in `/cache/nuget/terrabase/http-cache` on the runner.
  Each job extracts them into its own fresh packages folder, and locked restore checks every
  `.nupkg` against `packages.lock.json` (NU1403). A pull request therefore cannot plant files that a
  `main` release reuses (`scripts/ci/nuget-cache.sh`).
- `release` publishes only the server project (once per runtime); tests are built in `fast` alone.

## Requirements

- No secrets. `TerraScale.Endpoints` (ts-libs) restores as a package from the TerraScale
  Forgejo registry, which serves it without credentials; bump it in `Directory.Packages.props`.
- Releases use the job token (`forgejo.token`), which can write releases on pushes only; pull
  requests get a read-only job token.
